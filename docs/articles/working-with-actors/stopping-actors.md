---
title: Stopping actors
---
# Stopping actors
Actors are stopped by invoking the `Stop` method of a `ActorRefFactory`, i.e. `ActorContext` or `ActorSystem`. Typically the context is used for stopping child actors and the system for stopping top level actors. The actual termination of the actor is performed asynchronously, i.e. stop may return before the actor is stopped.

```csharp
public class MyStoppingActor : UntypedActor
{
    private IActorRef child;

    protected override void OnReceive(object message)
    {
        if (message.Equals("interrupt-child"))
        {
            Context.Stop(child);
        }
        else if (message is ReceiveTimeout)
        {
            Context.Stop(Self);
        }
        else
        {
            Unhandled(message);
        }
    }
}
```

Processing of the current message, if any, will continue before the actor is stopped, but additional messages in the mailbox will not be processed. By default these messages are sent to the `DeadLetters` of the `ActorSystem`, but that depends on the mailbox implementation.

Termination of an actor proceeds in two steps: first the actor suspends its mailbox processing and sends a stop command to all its children, then it keeps processing the internal termination notifications from its children until the last one is gone, finally terminating itself (invoking `PostStop`, dumping mailbox, publishing `Terminated` on the `DeathWatch`, telling its supervisor). This procedure ensures that actor system sub-trees terminate in an orderly fashion, propagating the stop command to the leaves and collecting their confirmation back to the stopped supervisor. If one of the actors does not respond (i.e. processing a message for extended periods of time and therefore not receiving the stop command), this whole process will be stuck.

Upon `ActorSystem.Terminate`, the system guardian actors will be stopped, and the aforementioned process will ensure proper termination of the whole system.

The `PostStop` hook is invoked after an actor is fully stopped. This enables cleaning up of resources:

```csharp
protected override void PostStop()
{
    // clean up resources here ...
}
```

> **Note**<br/>
Since stopping an actor is asynchronous, you cannot immediately reuse the name of the child you just stopped; this will result in an `InvalidActorNameException`. Instead, watch the terminating actor and create its replacement in response to the `Terminated` message which will eventually arrive.

## PoisonPill
You can also send an actor the `Akka.Actor.PoisonPill` message, which will stop the actor when the message is processed. `PoisonPill` is enqueued as ordinary messages and will be handled after messages that were already queued in the mailbox.

Use it like this:

```csharp
myActor.Tell(PoisonPill.Instance, Sender);
```

## Graceful Stop
`GracefulStop` is useful if you need to wait for termination or compose ordered termination of several actors:

```csharp
var manager = system.ActorOf<Manager>();

try
{
    await manager.GracefulStop(TimeSpan.FromMilliseconds(5), "shutdown");
    // the actor has been stopped
}
catch (TaskCanceledException)
{
    // the actor wasn't stopped within 5 seconds
}

...

public class Manager : UntypedActor
{
    private IActorRef worker = Context.Watch(Context.ActorOf<Worker>("worker"));

    protected override void OnReceive(object message)
    {
        if (message.Equals("job"))
        {
            worker.Tell("crunch", Self);
        }
        else if (message.Equals("shutdown"))
        {
            worker.Tell(PoisonPill.Instance, Self);
            Context.Become(ShuttingDown);
        }
    }

    private void ShuttingDown(object message)
    {
        if (message.Equals("job"))
        {
            Sender.Tell("service unavailable, shutting down", Self);
        }
        else if (message is Terminated)
        {
            Context.Stop(Self);
        }
    }
}
```

When `GracefulStop()` returns successfully, the actor’s `PostStop()` hook will have been executed: there exists a happens-before edge between the end of `PostStop()` and the return of `GracefulStop()`.

In the above example a `"shutdown"` message is sent to the target actor to initiate the process of stopping the actor. You can use `PoisonPill` for this, but then you have limited possibilities to perform interactions with other actors before stopping the target actor. Simple cleanup tasks can be handled in `PostStop`.

> **Warning**<br/>
Keep in mind that an actor stopping and its name being deregistered are separate events which happen asynchronously from each other. Therefore it may be that you will find the name still in use after `GracefulStop()` returned. In order to guarantee proper deregistration, only reuse names from within a supervisor you control and only in response to a `Terminated` message, i.e. not for top-level actors.
