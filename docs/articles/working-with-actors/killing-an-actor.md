---
title: Killing an Actor
---
# Killing an Actor
You can kill an actor by sending a `Kill` message. This will cause the actor to throw a `ActorKilledException`, triggering a failure. The actor will suspend operation and its supervisor will be asked how to handle the failure, which may mean resuming the actor, restarting it or terminating it completely. See [What Supervision Means](What Supervision Means) for more information.

Use `Kill` like this:

```csharp
// kill the 'victim' actor
victim.Tell(Akka.Actor.Kill.Instance, ActorRef.NoSender);
```