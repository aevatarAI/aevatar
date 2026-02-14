// ─────────────────────────────────────────────────────────────
// InMemoryStream - in-memory event stream implementation.
// Channel-based producer-consumer model with multi-subscriber broadcast.
// ─────────────────────────────────────────────────────────────

using System.Threading.Channels;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Streaming;

/// <summary>In-memory event stream for actor-to-actor delivery and broadcast.</summary>
public sealed class InMemoryStream : IStream
{
    private readonly Channel<EventEnvelope> _channel = Channel.CreateUnbounded<EventEnvelope>(
        new UnboundedChannelOptions { SingleReader = true });
    private volatile Func<EventEnvelope, Task>[] _subscribers = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readerLoop;

    /// <summary>Stream ID, typically an actor ID.</summary>
    public string StreamId { get; }

    /// <summary>Creates an in-memory stream with the specified ID.</summary>
    /// <param name="streamId">Unique stream identifier.</param>
    public InMemoryStream(string streamId)
    {
        StreamId = streamId;
        _readerLoop = Task.Run(ReaderLoopAsync);
    }

    /// <summary>Writes message into stream; non-EventEnvelope messages are auto-wrapped.</summary>
    /// <typeparam name="T">Protobuf message type.</typeparam>
    /// <param name="message">Message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
    {
        if (message is EventEnvelope envelope)
        { _channel.Writer.TryWrite(envelope); return Task.CompletedTask; }

        var wrapped = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(message),
            Direction = EventDirection.Down,
        };
        _channel.Writer.TryWrite(wrapped);
        return Task.CompletedTask;
    }

    /// <summary>Subscribes to stream and invokes handler for matching messages.</summary>
    /// <typeparam name="T">Expected message type.</typeparam>
    /// <param name="handler">Message handler.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Disposable subscription handle.</returns>
    public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default) where T : IMessage
    {
        Func<EventEnvelope, Task> envelopeHandler = async envelope =>
        {
            if (typeof(T) == typeof(EventEnvelope))
                await ((Func<EventEnvelope, Task>)(object)handler)(envelope);
        };

        lock (_lock)
        {
            var current = _subscribers;
            var next = new Func<EventEnvelope, Task>[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = envelopeHandler;
            _subscribers = next;
        }

        var sub = new StreamSubscription(() =>
        {
            lock (_lock) { _subscribers = _subscribers.Where(s => s != envelopeHandler).ToArray(); }
        });
        return Task.FromResult<IAsyncDisposable>(sub);
    }

    private async Task ReaderLoopAsync()
    {
        try
        {
            await foreach (var envelope in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                var subs = _subscribers;
                foreach (var sub in subs)
                    try { await sub(envelope); } catch { /* best-effort */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Shuts down stream, stops reader loop, and cancels subscriptions.</summary>
    public void Shutdown() { _channel.Writer.TryComplete(); _cts.Cancel(); }
}
