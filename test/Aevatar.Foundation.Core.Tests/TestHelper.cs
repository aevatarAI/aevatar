// TestHelper - testing utilities.

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests;

public static class TestHelper
{
    /// <summary>Creates an EventEnvelope containing the specified event.</summary>
    public static EventEnvelope Envelope<T>(T evt, string publisherId = "other") where T : IMessage =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = new EnvelopeRoute
            {
                PublisherActorId = publisherId,
                Direction = EventDirection.Down,
            },
        };
}

// Shared test agents

public class CounterAgent : TestGAgentBase<CounterState>
{
    public int HandleCount { get; private set; }

    [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
    public Task HandleIncrement(IncrementEvent evt)
    {
        State.Count += evt.Amount;
        HandleCount++;
        return Task.CompletedTask;
    }

    [Aevatar.Foundation.Abstractions.Attributes.EventHandler(Priority = 10)]
    public Task HandleDecrement(DecrementEvent evt)
    {
        State.Count -= evt.Amount;
        HandleCount++;
        return Task.CompletedTask;
    }
}

public class EmptyAgent : TestGAgentBase<CounterState>;

public class CollectorAgent : TestGAgentBase<CounterState>
{
    private readonly object _gate = new();
    private readonly List<(int Threshold, TaskCompletionSource<bool> Signal)> _waiters = [];

    public List<string> ReceivedMessages { get; } = [];

    [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
    public Task HandlePing(PingEvent evt) => RecordMessage(evt.Message);

    [Aevatar.Foundation.Abstractions.Attributes.EventHandler]
    public Task HandlePong(PongEvent evt) => RecordMessage(evt.Reply);

    public Task WaitForMessageCountAsync(int expectedCount, TimeSpan timeout)
    {
        Task waitTask;
        lock (_gate)
        {
            if (ReceivedMessages.Count >= expectedCount)
                return Task.CompletedTask;

            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((expectedCount, waiter));
            waitTask = waiter.Task;
        }

        return waitTask.WaitAsync(timeout);
    }

    private Task RecordMessage(string message)
    {
        lock (_gate)
        {
            ReceivedMessages.Add(message);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (ReceivedMessages.Count < waiter.Threshold)
                    continue;

                _waiters.RemoveAt(i);
                waiter.Signal.TrySetResult(true);
            }
        }

        return Task.CompletedTask;
    }
}

public class EchoAgent : TestGAgentBase<CounterState>;
