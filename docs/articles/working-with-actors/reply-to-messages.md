---
title: Reply to messages
---
# Reply to messages
If you want to have a handle for replying to a message, you can use `Sender`, which gives you an `IActorRef`. You can reply by sending to that `IActorRef` with `Sender.Tell(replyMsg, Self)`. You can also store the `IActorRef` for replying later, or passing on to other actors. If there is no sender (a message was sent without an actor or task context) then the sender defaults to a 'dead-letter' actor ref.

```csharp
protected override void OnReceive(object message)
{
  var result = calculateResult();

  // do not forget the second argument!
  Sender.Tell(result, Self);
}
```