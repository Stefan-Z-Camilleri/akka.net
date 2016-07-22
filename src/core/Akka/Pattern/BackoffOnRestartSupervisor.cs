//-----------------------------------------------------------------------
// <copyright file="BackoffOnRestartSupervisor.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using Akka.Actor;
using Akka.Event;
using Akka.Util.Internal;

namespace Akka.Pattern
{
    public class BackoffOnRestartSupervisor : ActorBase
    {
        private readonly Props _childProps;
        private readonly string _childName;
        private readonly TimeSpan _minBackoff;
        private readonly TimeSpan _maxBackoff;
        private readonly IBackoffReset _reset;
        private readonly double _randomFactor;
        private readonly OneForOneStrategy _strategy;
        private readonly ILoggingAdapter _log = Context.GetLogger();

        private IActorRef _child = null;
        private int _restartCount = 0;

        public BackoffOnRestartSupervisor(
            Props childProps,
            string childName,
            TimeSpan minBackoff,
            TimeSpan maxBackoff,
            IBackoffReset reset,
            double randomFactor,
            OneForOneStrategy strategy)
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

        protected override void PreStart()
        {
            StartChild();
            base.PreStart();
        }

        protected override SupervisorStrategy SupervisorStrategy()
        {
            var supervisorStrategy = new OneForOneStrategy(_strategy.MaxNumberOfRetries, _strategy.WithinTimeRangeMilliseconds,
                ex =>
                {
                    // TODO: StackOverflowException here
                    //var defaultDirective = base.SupervisorStrategyInternal.AsInstanceOf<OneForOneStrategy>().Decider?.Decide(ex) ?? Directive.Escalate;
                    var defaultDirective = Directive.Escalate;

                    var directive = _strategy.Decider?.Decide(ex) ?? defaultDirective;

                    // Whatever the final Directive is, we will translate all Restarts
                    // to our own Restarts, which involves stopping the child.
                    if (directive == Directive.Restart)
                    {
                        var childRef = Sender;
                        Become(WaitChildTerminatedBeforeBackoff(childRef));
                        return Directive.Stop;
                    }
                    return directive;
                });

            return supervisorStrategy;
        }

        protected override bool Receive(object message)
        {
            return OnTerminated(message) || HandleBackoff(message);
        }

        private Receive WaitChildTerminatedBeforeBackoff(IActorRef childRef)
        {
            return message => WaitChildTerminatedBeforeBackoff(message, childRef) || HandleBackoff(message);
        }

        private bool HandleBackoff(object message)
        {
            if (message is BackoffSupervisor.StartChild)
            {
                StartChild();
                var backoffReset = _reset as AutoReset;
                if (backoffReset != null)
                {
                    Context.System.Scheduler.ScheduleTellOnce(backoffReset.ResetBackoff, Self,
                        new BackoffSupervisor.ResetRestartCount(_restartCount), Self);
                }
            }
            else if (message is BackoffSupervisor.Reset)
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
            else if (message is BackoffSupervisor.ResetRestartCount)
            {
                var restartCount = (BackoffSupervisor.ResetRestartCount)message;
                if (restartCount.Current == _restartCount) _restartCount = 0;
            }
            else if (message is BackoffSupervisor.GetRestartCount)
            {
                Sender.Tell(new BackoffSupervisor.RestartCount(_restartCount));
            }
            else if (message is BackoffSupervisor.GetCurrentChild)
            {
                Sender.Tell(new BackoffSupervisor.CurrentChild(_child));
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

            return true;
        }

        private bool OnTerminated(object message)
        {
            if (message is Terminated)
            {
                var terminated = (Terminated)message;
                _log.Debug($"Terminating, because child {terminated.ActorRef} terminated itself");
                Context.Stop(Self);
                return true;
            }

            return false;
        }

        private bool WaitChildTerminatedBeforeBackoff(object message, IActorRef childRef)
        {
            var terminated = message as Terminated;
            if (terminated != null && terminated.ActorRef.Equals(childRef))
            {
                Become(Receive);
                _child = null;
                var restartDelay = BackoffSupervisor.CalculateDelay(_restartCount, _minBackoff, _maxBackoff, _randomFactor);
                Context.System.Scheduler.ScheduleTellOnce(restartDelay, Self, BackoffSupervisor.StartChild.Instance, Self);
                _restartCount++;
            }
            else if (message is BackoffSupervisor.StartChild)
            {
                // Ignore it, we will schedule a new one once current child terminated.
            }
            else
            {
                return false;
            }

            return true;
        }

        private void StartChild()
        {
            if (_child == null)
            {
                _child = Context.Watch(Context.ActorOf(_childProps, _childName));
            }
        }
    }
}
