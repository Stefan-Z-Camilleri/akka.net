//-----------------------------------------------------------------------
// <copyright file="BackoffOnRestartSupervisorSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2016 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2016 Akka.NET project <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using Akka.Actor;
using Akka.Pattern;
using Akka.TestKit;
using Xunit;
using FluentAssertions;

namespace Akka.Tests.Pattern
{
    public class BackoffOnRestartSupervisorSpec : AkkaSpec
    {
        #region Infrastructure
        public class TestException : Exception
        {
            public TestException(string message) : base(message)
            {
            }
        }

        public class StoppingException : TestException
        {
            public StoppingException() : base("stopping exception")
            {
            }
        }

        public class NormalException : TestException
        {
            public NormalException() : base("normal exception")
            {
            }
        }

        public class TestActor : ReceiveActor
        {
            public TestActor(IActorRef probe)
            {
                probe.Tell("STARTED");

                Receive<string>(str => str.Equals("DIE"), msg => Context.Stop(Self));
                Receive<string>(str => str.Equals("THROW"), msg =>
                {
                    throw new NormalException();
                    return;
                });
                Receive<string>(str => str.Equals("THROW_STOPPING_EXCEPTION"), msg =>
                {
                    throw new StoppingException();
                    return;
                });
                Receive<Tuple<string, object>>(str => str.Item1.Equals("TO_PARENT"), msg =>
                {
                    Context.Parent.Tell(msg.Item2);
                });
                ReceiveAny(other => probe.Tell(other));
            }

            public static Props Props(IActorRef probe)
            {
                return Akka.Actor.Props.Create(() => new TestActor(probe));
            }
        }

        public class TestParentActor : ReceiveActor
        {
            public TestParentActor(IActorRef probe, Props supervisorProps)
            {
                var supervisor = Context.ActorOf(supervisorProps);

                ReceiveAny(other => probe.Forward(other));
            }

            public static Props Props(IActorRef probe, Props supervisorProps)
            {
                return Akka.Actor.Props.Create(() => new TestParentActor(probe, supervisorProps));
            }
        }

        public class SlowlyFailingActor : ReceiveActor
        {
            private readonly TestLatch _latch;

            public SlowlyFailingActor(TestLatch latch)
            {
                _latch = latch;

                Receive<string>(str => str.Equals("THROW"), msg =>
                {
                    Sender.Tell("THROWN");
                    throw new NormalException();
                    return;
                });
                Receive<string>(str => str.Equals("PING"), msg =>
                {
                    Sender.Tell("PONG");
                });
            }

            protected override void PostStop()
            {
                _latch.Ready(3.Seconds());
            }

            public static Props Props(TestLatch latch)
            {
                return Akka.Actor.Props.Create(() => new SlowlyFailingActor(latch));
            }
        }

        private Props SupervisorProps(IActorRef probeRef)
        {
            var options = Backoff.OnFailure(TestActor.Props(probeRef), "someChildName", 200.Milliseconds(), 10.Seconds(), 0.0)
                .WithSupervisorStrategy(new OneForOneStrategy(ex =>
                {
                    if (ex is StoppingException)
                    {
                        return Directive.Stop;
                    }
                    return Directive.Escalate;
                }));

            return BackoffSupervisor.Props(options);
        }
        #endregion

        [Fact(Skip = "Not working")]
        public void BackoffOnRestartSupervisor_must_terminate_when_child_terminates()
        {
            var probe = CreateTestProbe();
            var supervisor = Sys.ActorOf(SupervisorProps(probe.Ref));
            probe.ExpectMsg("STARTED");

            //EventFilter.Exception<TestException>().Expect(1, () =>
            //{
                probe.Watch(supervisor);
                supervisor.Tell("DIE");
                probe.ExpectTerminated(supervisor);
            //});
        }

        [Fact(Skip = "Not working")]
        public void BackoffOnRestartSupervisor_must_restart_the_child_with_an_exponential_back_off()
        {
            var probe = CreateTestProbe();
            var supervisor = Sys.ActorOf(SupervisorProps(probe.Ref));
            probe.ExpectMsg("STARTED");

            //EventFilter.Exception<TestException>().Expect(1, () =>
            //{
                // Exponential back off restart test
                probe.Within(TimeSpan.FromSeconds(1.4), 2.Seconds(), () =>
                {
                    supervisor.Tell("THROW");
                    // numRestart = 0 ~ 200 millis
                    probe.ExpectMsg<string>(300.Milliseconds(), "STARTED");

                    supervisor.Tell("THROW");
                    // numRestart = 1 ~ 400 millis
                    probe.ExpectMsg<string>(500.Milliseconds(), "STARTED");

                    supervisor.Tell("THROW");
                    // numRestart = 2 ~ 800 millis
                    probe.ExpectMsg<string>(900.Milliseconds(), "STARTED");
                });
            //});

            // Verify that we only have one child at this point by selecting all the children
            // under the supervisor and broadcasting to them.
            // If there exists more than one child, we will get more than one reply.
            var supervisionChildSelection = Sys.ActorSelection(supervisor.Path / "*");
            supervisionChildSelection.Tell("testmsg", probe.Ref);
            probe.ExpectMsg("testmsg");
            probe.ExpectNoMsg();
        }

        [Fact(Skip = "Not working")]
        public void BackoffOnRestartSupervisor_must_stop_on_exceptions_as_dictated_by_the_supervisor_strategy()
        {
            var probe = CreateTestProbe();
            var supervisor = Sys.ActorOf(SupervisorProps(probe.Ref));
            probe.ExpectMsg("STARTED");

            EventFilter.Exception<TestException>().Expect(1, () =>
            {
                // This should cause the supervisor to stop the child actor and then
                // subsequently stop itself.
                supervisor.Tell("THROW_STOPPING_EXCEPTION");
                probe.ExpectTerminated(supervisor);
            });
        }

        [Fact(Skip = "Not working")]
        public void BackoffOnRestartSupervisor_must_forward_messages_from_the_child_to_the_parent_of_the_supervisor()
        {
            var probe = CreateTestProbe();
            var parent = Sys.ActorOf(TestParentActor.Props(probe.Ref, SupervisorProps(probe.Ref)));
            probe.ExpectMsg("STARTED");
            var child = probe.LastSender;

            child.Tell(Tuple.Create("TO_PARENT", "TEST_MESSAGE"));
            probe.ExpectMsg("TEST_MESSAGE");
        }

        [Fact(Skip = "Not working")]
        public void BackoffOnRestartSupervisor_must_accept_commands_while_child_is_terminating()
        {
            // TODO: is the same as CountDownLatch?
            var postStopLatch = CreateTestLatch(1);
            // TODO: Supervising strategy doesn't have loggingEnabled in constructor
            // TODO: is nanos the same as ticks?
            var options = Backoff.OnFailure(SlowlyFailingActor.Props(postStopLatch), "someChildName", 1.Ticks(), 1.Ticks(), 0.0)
                .WithSupervisorStrategy(new OneForOneStrategy(ex =>
                {
                    if (ex is StoppingException)
                    {
                        return Directive.Stop;
                    }
                    return Directive.Escalate;
                }));
            var supervisor = Sys.ActorOf(BackoffSupervisor.Props(options));

            supervisor.Tell(BackoffSupervisor.GetCurrentChild.Instance);
            // new instance
            var child = ExpectMsg<BackoffSupervisor.CurrentChild>().Ref;

            child.Tell("PING");
            ExpectMsg("PONG");

            supervisor.Tell("THROW");
            ExpectMsg("THROWN");

            child.Tell("PING");
            ExpectNoMsg(100.Milliseconds()); // Child is in limbo due to latch in postStop. There is no Terminated message yet

            supervisor.Tell(BackoffSupervisor.GetCurrentChild.Instance);
            ExpectMsg<BackoffSupervisor.CurrentChild>().Ref.Should().NotBeSameAs(child);

            supervisor.Tell(BackoffSupervisor.GetRestartCount.Instance);
            ExpectMsg<BackoffSupervisor.RestartCount>().Count.Should().Be(0);

            postStopLatch.CountDown();

            // New child is ready
            AwaitAssert(() =>
            {
                supervisor.Tell(BackoffSupervisor.GetCurrentChild.Instance);
                // new instance
                ExpectMsg<BackoffSupervisor.CurrentChild>().Ref.Should().NotBeSameAs(child);
            });
        }
    }
}
