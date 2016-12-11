---
title: Messages and Immutability
---
# Messages and Immutability
**IMPORTANT:** Messages can be any kind of object but have to be immutable. Akka can’t enforce immutability (yet) so this has to be by convention.

Here is an example of an immutable message:

```csharp
public class ImmutableMessage
{
    public ImmutableMessage(int sequenceNumber, List<string> values)
    {
        SequenceNumber = sequenceNumber;
        Values = values.AsReadOnly();
    }

    public int SequenceNumber { get; }
    public IReadOnlyCollection<string> Values { get; }
}
```