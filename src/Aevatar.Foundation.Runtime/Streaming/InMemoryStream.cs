// ─────────────────────────────────────────────────────────────
// InMemoryStream - in-memory event stream implementation.
// Channel-based producer-consumer model with multi-subscriber broadcast.
// ─────────────────────────────────────────────────────────────

using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.Foundation.Runtime.Streaming;

/// <summary>In-memory event stream for actor-to-actor delivery and broadcast.</summary>
public sealed class InMemoryStream : IStream
{
    private readonly Channel<EventEnvelope> _ingressChannel;
    private readonly Channel<EventEnvelope> _dispatchChannel;
    private volatile Func<EventEnvelope, Task>[] _subscribers = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pumpLoop;
    private readonly Task _dispatchLoop;
    private readonly InMemoryStreamOptions _options;
    private readonly ILogger<InMemoryStream> _logger;
    private readonly Func<EventEnvelope, Task>? _onDispatchedAsync;
    private readonly Func<StreamForwardingBinding, CancellationToken, Task> _upsertRelayAsync;
    private readonly Func<string, CancellationToken, Task> _removeRelayAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<StreamForwardingBinding>>> _listRelaysAsync;

    /// <summary>Stream ID, typically an actor ID.</summary>
    public string StreamId { get; }

    /// <summary>Creates an in-memory stream with the specified ID.</summary>
    /// <param name="streamId">Unique stream identifier.</param>
    public InMemoryStream(
        string streamId,
        InMemoryStreamOptions? options = null,
        ILogger<InMemoryStream>? logger = null,
        Func<EventEnvelope, Task>? onDispatchedAsync = null,
        Func<StreamForwardingBinding, CancellationToken, Task>? upsertRelayAsync = null,
        Func<string, CancellationToken, Task>? removeRelayAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<StreamForwardingBinding>>>? listRelaysAsync = null)
    {
        _options = options ?? new InMemoryStreamOptions();
        var capacity = _options.Capacity > 0 ? _options.Capacity : 4096;
        _ingressChannel = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(capacity)
        {
            FullMode = _options.FullMode,
            SingleReader = true,
            SingleWriter = false,
        });
        _dispatchChannel = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        StreamId = streamId;
        _logger = logger ?? NullLogger<InMemoryStream>.Instance;
        _onDispatchedAsync = onDispatchedAsync;
        _upsertRelayAsync = upsertRelayAsync ?? ((_, _) => Task.CompletedTask);
        _removeRelayAsync = removeRelayAsync ?? ((_, _) => Task.CompletedTask);
        _listRelaysAsync = listRelaysAsync ?? (_ => Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]));
        _pumpLoop = Task.Run(PumpLoopAsync);
        _dispatchLoop = Task.Run(DispatchLoopAsync);
    }

    /// <summary>Writes message into stream; non-EventEnvelope messages are auto-wrapped.</summary>
    /// <typeparam name="T">Protobuf message type.</typeparam>
    /// <param name="message">Message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
    {
        if (message is EventEnvelope envelope)
            return _ingressChannel.Writer.WriteAsync(envelope, ct).AsTask();

        var wrapped = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(message),
            Route = EnvelopeRouteSemantics.CreateBroadcast(string.Empty, BroadcastDirection.Down),
        };
        return _ingressChannel.Writer.WriteAsync(wrapped, ct).AsTask();
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

    public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ct.ThrowIfCancellationRequested();

        var normalized = CloneBindingForCurrentStream(binding);
        return _upsertRelayAsync(normalized, ct);
    }

    public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);
        ct.ThrowIfCancellationRequested();
        return _removeRelayAsync(targetStreamId, ct);
    }

    public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _listRelaysAsync(ct);
    }

    private async Task PumpLoopAsync()
    {
        try
        {
            await foreach (var envelope in _ingressChannel.Reader.ReadAllAsync(_cts.Token))
            {
                await _dispatchChannel.Writer.WriteAsync(envelope, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally
        {
            _dispatchChannel.Writer.TryComplete();
        }
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var envelope in _dispatchChannel.Reader.ReadAllAsync(_cts.Token))
            {
                var subs = _subscribers;
                if (_options.DispatchSubscribersConcurrently)
                {
                    foreach (var sub in subs)
                    {
                        _ = Task.Run(() => InvokeSubscriberAsync(sub, envelope), CancellationToken.None);
                    }

                    if (!await InvokePostDispatchAsync(envelope))
                        return;

                    continue;
                }

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
                        {
                            StopWithError(ex);
                            return;
                        }
                    }
                }

                if (!await InvokePostDispatchAsync(envelope))
                    return;
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
    }

    private async Task InvokeSubscriberAsync(Func<EventEnvelope, Task> sub, EventEnvelope envelope)
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
            {
                StopWithError(ex);
            }
        }
    }

    private async Task<bool> InvokePostDispatchAsync(EventEnvelope envelope)
    {
        if (_onDispatchedAsync == null)
            return true;

        try
        {
            await _onDispatchedAsync(envelope);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "In-memory stream post-dispatch forwarding failed. stream={StreamId}",
                StreamId);

            if (_options.ThrowOnSubscriberError)
            {
                StopWithError(ex);
                return false;
            }

            return true;
        }
    }

    private void StopWithError(Exception ex)
    {
        _ingressChannel.Writer.TryComplete(ex);
        _dispatchChannel.Writer.TryComplete(ex);
        _cts.Cancel();
    }

    /// <summary>Shuts down stream, stops reader loop, and cancels subscriptions.</summary>
    public void Shutdown()
    {
        _ingressChannel.Writer.TryComplete();
        _dispatchChannel.Writer.TryComplete();
        _cts.Cancel();
    }

    private StreamForwardingBinding CloneBindingForCurrentStream(StreamForwardingBinding binding) =>
        new()
        {
            SourceStreamId = StreamId,
            TargetStreamId = binding.TargetStreamId,
            ForwardingMode = binding.ForwardingMode,
            DirectionFilter = new HashSet<BroadcastDirection>(binding.DirectionFilter),
            EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
            Version = binding.Version,
            LeaseId = binding.LeaseId,
        };
}
