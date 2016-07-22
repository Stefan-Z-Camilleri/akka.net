//-----------------------------------------------------------------------
// <copyright file="BackoffSupervisor.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using Akka.Actor;
using Akka.Event;
using Akka.Util;

namespace Akka.Pattern
{
    /// <summary>
    /// Actor used to supervise actors with ability to restart them after back-off timeout occurred. 
    /// It's designed for cases when i.e. persistent actor stops due to journal unavailability or failure. 
    /// In this case it better to wait before restart.
    /// </summary>
    public class BackoffSupervisor : UntypedActor
    {
        #region Messages

        /// <summary>
        /// Send this message to the <see cref="BackoffSupervisor"/> and it will reply with <see cref="CurrentChild"/> containing the `ActorRef` of the current child, if any.
        /// </summary>
        [Serializable]
        public sealed class GetCurrentChild
        {
            public static readonly GetCurrentChild Instance = new GetCurrentChild();
            private GetCurrentChild() { }
        }

        /// <summary>
        /// Send this message to the <see cref="BackoffSupervisor"/> and it will reply with <see cref="CurrentChild"/> containing the `ActorRef` of the current child, if any.
        /// </summary>
        [Serializable]
        public sealed class CurrentChild
        {
            public CurrentChild(IActorRef @ref)
            {
                Ref = @ref;
            }

            public IActorRef Ref { get; }
        }

        /// <summary>
        /// Send this message to the <see cref="BackoffSupervisor"/> and it will reset the back-off. This should be used in conjunction with `withManualReset` in <see cref="BackoffOptionsImpl"/>.
        /// </summary>
        [Serializable]
        public sealed class Reset
        {
            public static readonly Reset Instance = new Reset();
            private Reset() { }
        }

        /// <summary>
        /// Send this message to the <see cref="BackoffSupervisor"/> and it will reply with <see cref="BackoffSupervisor.RestartCount"/> containing the current restart count.
        /// </summary>
        [Serializable]
        public sealed class GetRestartCount
        {
            public static readonly GetRestartCount Instance = new GetRestartCount();
            private GetRestartCount() { }
        }

        [Serializable]
        public sealed class RestartCount
        {
            public RestartCount(int count)
            {
                Count = count;
            }

            public int Count { get; }
        }

        /// <summary>
        /// TODO: should implement IDeadLetterSupression
        /// </summary>
        [Serializable]
        public sealed class StartChild : IDeadLetterSuppression
        {
            public static readonly StartChild Instance = new StartChild();
            private StartChild() { }
        }

        /// <summary>
        /// TODO: should implement IDeadLetterSupression
        /// </summary>
        [Serializable]
        public sealed class ResetRestartCount : IDeadLetterSuppression
        {
            public ResetRestartCount(int current)
            {
                Current = current;
            }

            public int Current { get; }
        }

        #endregion

        private readonly Props _childProps;
        private readonly string _childName;
        private readonly TimeSpan _minBackoff;
        private readonly TimeSpan _maxBackoff;
        private readonly IBackoffReset _reset;
        private readonly double _randomFactor;
        private readonly SupervisorStrategy _strategy;
        private int _restartCount = 0;
        private IActorRef _child = null;

        public BackoffSupervisor(
            Props childProps,
            string childName,
            TimeSpan minBackoff,
            TimeSpan maxBackoff,
            double randomFactor)
            : this(childProps, childName, minBackoff, maxBackoff, new AutoReset(minBackoff), randomFactor, Actor.SupervisorStrategy.DefaultStrategy)
        {
        }

        public BackoffSupervisor(
            Props childProps,
            string childName,
            TimeSpan minBackoff,
            TimeSpan maxBackoff,
            double randomFactor,
            SupervisorStrategy strategy)
            : this(childProps, childName, minBackoff, maxBackoff, new AutoReset(minBackoff), randomFactor, strategy)
        {
        }

        public BackoffSupervisor(
            Props childProps,
            string childName,
            TimeSpan minBackoff,
            TimeSpan maxBackoff,
            IBackoffReset reset,
            double randomFactor,
            SupervisorStrategy strategy)
        {
            if (minBackoff <= TimeSpan.Zero) throw new ArgumentException("MinBackoff must be greater than 0");
            if (maxBackoff < minBackoff) throw new ArgumentException("MaxBackoff must be greater than MinBackoff");
            if (randomFactor < 0.0 || randomFactor > 1.0) throw new ArgumentException("RandomFactor must be between 0.0 and 1.0");

            _childProps = childProps;
            _childName = childName;
            _minBackoff = minBackoff;
            _maxBackoff = maxBackoff;
            _reset = reset;
            _randomFactor = randomFactor;
            _strategy = strategy;
        }

        protected override SupervisorStrategy SupervisorStrategy()
        {
            var oneForOne = _strategy as OneForOneStrategy;
            if (oneForOne != null)
            {
                return new OneForOneStrategy(
                    oneForOne.MaxNumberOfRetries,
                    oneForOne.WithinTimeRangeMilliseconds,
                    ex =>
                    {
                        // TODO: fix here
                        return oneForOne.Decider.Decide(ex);
                    });
            }
            else
            {
                return _strategy;
            }
        }

        protected override void PreStart()
        {
            StartChildActor();
            base.PreStart();
        }

        protected override void OnReceive(object message)
        {
            if (message is Terminated)
            {
                var terminated = (Terminated)message;
                if (_child != null && _child.Equals(terminated.ActorRef))
                {
                    _child = null;
                    var restartDelay = CalculateDelay(_restartCount, _minBackoff, _maxBackoff, _randomFactor);
                    Context.System.Scheduler.ScheduleTellOnce(restartDelay, Self, StartChild.Instance, Self);
                    _restartCount++;
                }
                else Unhandled(message);
            }
            else if (message is StartChild)
            {
                StartChildActor();
                var backoffReset = _reset as AutoReset;
                if (backoffReset != null)
                {
                    Context.System.Scheduler.ScheduleTellOnce(backoffReset.ResetBackoff, Self, new ResetRestartCount(_restartCount), Self);
                }
            }
            else if (message is Reset)
            {
                if (_reset is ManualReset)
                {
                    _restartCount = 0;
                }
                else
                {
                    Unhandled(message);
                }
            }
            else if (message is ResetRestartCount)
            {
                var restartCount = (ResetRestartCount)message;
                if (restartCount.Current == _restartCount) _restartCount = 0;
            }
            else if (message is GetRestartCount)
            {
                Sender.Tell(new RestartCount(_restartCount));
            }
            else if (message is GetCurrentChild)
            {
                Sender.Tell(new CurrentChild(_child));
            }
            else
            {
                if (_child.Equals(Sender))
                {
                    // use the BackoffSupervisor as sender
                    Context.Parent.Tell(message);
                }
                else
                {
                    if (_child != null) _child.Forward(message);
                    else Context.System.DeadLetters.Forward(message);
                }
            }
        }

        private void StartChildActor()
        {
            if (_child == null)
            {
                _child = Context.Watch(Context.ActorOf(_childProps, _childName));
            }
        }

        /// <summary>
        /// Props for creating a <see cref="BackoffSupervisor"/> actor.
        /// </summary>
        /// <param name="childProps">The <see cref="Akka.Actor.Props"/> of the child actor that will be started and supervised</param>
        /// <param name="childName">Name of the child actor</param>
        /// <param name="minBackoff">Minimum (initial) duration until the child actor will started again, if it is terminated</param>
        /// <param name="maxBackoff">The exponential back-off is capped to this duration</param>
        /// <param name="randomFactor">After calculation of the exponential back-off an additional random delay based on this factor is added, e.g. `0.2` adds up to `20%` delay. In order to skip this additional delay pass in `0`.</param>
        public static Props Props(
            Props childProps,
            string childName,
            TimeSpan minBackoff,
            TimeSpan maxBackoff,
            double randomFactor)
        {
            return PropsWithSupervisorStrategy(childProps, childName, minBackoff, maxBackoff, randomFactor,
                Actor.SupervisorStrategy.DefaultStrategy);
        }

        /// <summary>
        /// Props for creating a <see cref="BackoffSupervisor"/> actor from <see cref="BackoffOptionsImpl"/>.
        /// </summary>
        /// <param name="options">The <see cref="BackoffOptionsImpl"/> that specify how to construct a backoff-supervisor.</param>
        /// <returns></returns>
        public static Props Props(BackoffOptions options)
        {
            return options.Props;
        }

        /// <summary>
        /// Props for creating a <see cref="BackoffSupervisor"/> actor with a custom supervision strategy.
        /// </summary>
        /// <param name="childProps">The <see cref="Akka.Actor.Props"/> of the child actor that will be started and supervised</param>
        /// <param name="childName">Name of the child actor</param>
        /// <param name="minBackoff">Minimum (initial) duration until the child actor will started again, if it is terminated</param>
        /// <param name="maxBackoff">The exponential back-off is capped to this duration</param>
        /// <param name="randomFactor">After calculation of the exponential back-off an additional random delay based on this factor is added, e.g. `0.2` adds up to `20%` delay. In order to skip this additional delay pass in `0`.</param>
        /// <param name="strategy">The supervision strategy to use for handling exceptions in the child</param>
        public static Props PropsWithSupervisorStrategy(
            Props childProps,
            string childName,
            TimeSpan minBackoff,
            TimeSpan maxBackoff,
            double randomFactor,
            SupervisorStrategy strategy)
        {
             return Actor.Props.Create(
                () => new BackoffSupervisor(childProps, childName, minBackoff, maxBackoff, randomFactor, strategy));
        }

        internal static TimeSpan CalculateDelay(
            int restartCount,
            TimeSpan minBackoff,
            TimeSpan maxBackoff,
            double randomFactor)
        {
            var rand = 1.0 + ThreadLocalRandom.Current.NextDouble() * randomFactor;
            if (restartCount >= 30)
            {
                return maxBackoff; // duration overflow protection (> 100 years)
            }
            else
            {
                var max = Math.Min(maxBackoff.Ticks, minBackoff.Ticks * Math.Pow(2, restartCount)) * rand;
                if (max >= double.MaxValue)
                {
                    return maxBackoff;
                }
                else
                {
                    return new TimeSpan((long)max);
                }
            }
        }
    }
}