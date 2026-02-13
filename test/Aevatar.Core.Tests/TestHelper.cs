// TestHelper - testing utilities.

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Tests;

public static class TestHelper
{
    /// <summary>Creates an EventEnvelope containing the specified event.</summary>
    public static EventEnvelope Envelope<T>(T evt, string publisherId = "other") where T : IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = publisherId,
            Direction = EventDirection.Down,
        };
}

// Shared test agents

public class CounterAgent : GAgentBase<CounterState>
{
    public int HandleCount { get; private set; }

    [Aevatar.Attributes.EventHandler]
    public Task HandleIncrement(IncrementEvent evt)
    {
        State.Count += evt.Amount;
        HandleCount++;
        return Task.CompletedTask;
    }

    [Aevatar.Attributes.EventHandler(Priority = 10)]
    public Task HandleDecrement(DecrementEvent evt)
    {
        State.Count -= evt.Amount;
        HandleCount++;
        return Task.CompletedTask;
    }
}

public class EmptyAgent : GAgentBase<CounterState>;

public class CollectorAgent : GAgentBase<CounterState>
{
    public List<string> ReceivedMessages { get; } = [];

    [Aevatar.Attributes.EventHandler]
    public Task HandlePing(PingEvent evt) { ReceivedMessages.Add(evt.Message); return Task.CompletedTask; }

    [Aevatar.Attributes.EventHandler]
    public Task HandlePong(PongEvent evt) { ReceivedMessages.Add(evt.Reply); return Task.CompletedTask; }
}

public class EchoAgent : GAgentBase<CounterState>;
