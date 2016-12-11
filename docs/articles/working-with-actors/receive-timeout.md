---
title: Receive timeout
---
# Receive timeout
The `IActorContext` `SetReceiveTimeout` defines the inactivity timeout after which the sending of a `ReceiveTimeout` message is triggered. When specified, the receive function should be able to handle an `Akka.Actor.ReceiveTimeout` message.

> **Note**<br/>
Please note that the receive timeout might fire and enqueue the `ReceiveTimeout` message right after another message was enqueued; hence it is not guaranteed that upon reception of the receive timeout there must have been an idle period beforehand as configured via this method.

Once set, the receive timeout stays in effect (i.e. continues firing repeatedly after inactivity periods). Pass in `null` to `SetReceiveTimeout` to switch off this feature.

```csharp
public class MyUntypedActor : UntypedActor
{
    public MyUntypedActor()
    {
        // To set an initial delay
        SetReceiveTimeout(TimeSpan.FromSeconds(30));
    }

    protected override void OnReceive(object message)
    {
        if (message.Equals("Hello"))
        {
            // To set in a response to a message
            SetReceiveTimeout(TimeSpan.FromSeconds(1));
        }
        else if (message is ReceiveTimeout)
        {
            // To turn it off
            Context.SetReceiveTimeout(null);
        }
        else
        {
            Unhandled(message);
        }
    }
}
```