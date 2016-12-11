---
title: Become/Unbecome
---
# Become/Unbecome
## Upgrade
Akka supports hotswapping the Actor’s message loop (e.g. its implementation) at runtime. Use the `Context.Become` method from within the Actor. The hotswapped code is kept in a `Stack` which can be pushed (replacing or adding at the top) and popped.

> **Warning**<br/>
Please note that the actor will revert to its original behavior when restarted by its Supervisor.

To hotswap the Actor using `Context.Become`:

```csharp
public class HotSwapActor : UntypedActor
{
    protected override void OnReceive(object message)
    {
        if (message.Equals("angry"))
        {
            Become(Angry);
        }
        else if (message.Equals("happy"))
        {
            Become(Happy);
        }
        else
        {
            Unhandled(message);
        }
    }

    private void Angry(object message)
    {
        if (message.Equals("angry"))
        {
            Sender.Tell("I am already angry!", Self);
        }
        else if (message.Equals("happy"))
        {
            Become(Happy);
        }
    }

    private void Happy(object message)
    {
        if (message.Equals("happy"))
        {
            Sender.Tell("I am already happy :-)", Self);
        }
        else if (message.Equals("angry"))
        {
            Become(Angry);
        }
    }
}
```

This variant of the `Become` method is useful for many different things, such as to implement a Finite State Machine (FSM). It will replace the current behavior (i.e. the top of the behavior stack), which means that you do not use Unbecome, instead always the next behavior is explicitly installed.

The other way of using `Become` does not replace but add to the top of the behavior stack. In this case care must be taken to ensure that the number of “pop” operations (i.e. `Unbecome`) matches the number of “push” ones in the long run, otherwise this amounts to a memory leak (which is why this behavior is not the default).

```csharp
public class Swapper : UntypedActor
{
    public static readonly object SWAP = new object();

    private ILoggingAdapter log = Context.GetLogger();

    protected override void OnReceive(object message)
    {
        if (message == SWAP)
        {
            log.Info("Hi");

            Become(m =>
            {
                log.Info("Ho");
                Unbecome();
            }, false);
        }
        else
        {
            Unhandled(message);
        }
    }
}
...

static void Main(string[] args)
{
    var system = ActorSystem.Create("MySystem");
    var swapper = system.ActorOf<Swapper>();

    swapper.Tell(Swapper.SWAP);
    swapper.Tell(Swapper.SWAP);
    swapper.Tell(Swapper.SWAP);
    swapper.Tell(Swapper.SWAP);
    swapper.Tell(Swapper.SWAP);
    swapper.Tell(Swapper.SWAP);

    Console.ReadLine();
}
```