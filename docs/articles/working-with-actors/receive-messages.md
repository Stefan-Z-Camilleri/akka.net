---
title: Receive messages
---
# Receive messages
When an actor receives a message it is passed into the `OnReceive` method, this is an abstract method on the `UntypedActor` base class that needs to be defined.

Here is an example:
```csharp
public class MyUntypedActor : UntypedActor
{
    private ILoggingAdapter log = Context.GetLogger();

    protected override void OnReceive(object message)
    {
        if (message is string)
        {
            log.Info("Received String message: {0}", message);
            Sender.Tell(message, Self);
        }
        else
        {
            Unhandled(message);
        }
    }
}
```