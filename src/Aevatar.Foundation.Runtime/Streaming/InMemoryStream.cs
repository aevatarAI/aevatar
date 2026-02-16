// ─────────────────────────────────────────────────────────────
// InMemoryStream - in-memory event stream implementation.
// Channel-based producer-consumer model with multi-subscriber broadcast.
// ─────────────────────────────────────────────────────────────

using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Streaming;

/// <summary>In-memory event stream for actor-to-actor delivery and broadcast.</summary>
public sealed class InMemoryStream : IStream
{
    private readonly Channel<EventEnvelope> _channel;
    private volatile Func<EventEnvelope, Task>[] _subscribers = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readerLoop;
    private readonly InMemoryStreamOptions _options;
    private readonly ILogger<InMemoryStream> _logger;

    /// <summary>Stream ID, typically an actor ID.</summary>
    public string StreamId { get; }

    /// <summary>Creates an in-memory stream with the specified ID.</summary>
    /// <param name="streamId">Unique stream identifier.</param>
    public InMemoryStream(
        string streamId,
        InMemoryStreamOptions? options = null,
        ILogger<InMemoryStream>? logger = null)
    {
        _options = options ?? new InMemoryStreamOptions();
        var capacity = _options.Capacity > 0 ? _options.Capacity : 4096;
        _channel = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(capacity)
        {
            FullMode = _options.FullMode,
            SingleReader = true,
            SingleWriter = false,
        });
        StreamId = streamId;
        _logger = logger ?? NullLogger<InMemoryStream>.Instance;
        _readerLoop = Task.Run(ReaderLoopAsync);
    }

    /// <summary>Writes message into stream; non-EventEnvelope messages are auto-wrapped.</summary>
    /// <typeparam name="T">Protobuf message type.</typeparam>
    /// <param name="message">Message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
    {
        if (message is EventEnvelope envelope)
            return _channel.Writer.WriteAsync(envelope, ct).AsTask();

        var wrapped = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(message),
            Direction = EventDirection.Down,
        };
        return _channel.Writer.WriteAsync(wrapped, ct).AsTask();
    }

    /// <summary>Subscribes to stream and invokes handler for matching messages.</summary>
    /// <typeparam name="T">Expected message type.</typeparam>
    /// <param name="handler">Message handler.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Disposable subscription handle.</returns>
    public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default) where T : IMessage, new()
    {
        var descriptor = typeof(T) == typeof(EventEnvelope) ? null : new T().Descriptor;

        Func<EventEnvelope, Task> envelopeHandler = async envelope =>
        {
            if (typeof(T) == typeof(EventEnvelope))
            {
                await ((Func<EventEnvelope, Task>)(object)handler)(envelope);
                return;
            }

            var payload = envelope.Payload;
            if (payload == null) return;

            if (descriptor == null || !payload.Is(descriptor)) return;

            await handler(payload.Unpack<T>());
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
                {
                    try
                    {
                        await sub(envelope);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "In-memory stream subscriber failed. stream={StreamId}",
                            StreamId);

                        if (_options.ThrowOnSubscriberError)
                            throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    /// <summary>Shuts down stream, stops reader loop, and cancels subscriptions.</summary>
    public void Shutdown() { _channel.Writer.TryComplete(); _cts.Cancel(); }
}
