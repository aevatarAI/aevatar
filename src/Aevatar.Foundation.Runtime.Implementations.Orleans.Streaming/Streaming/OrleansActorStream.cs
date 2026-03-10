using Google.Protobuf.WellKnownTypes;
using Google.Protobuf.Reflection;
using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

internal sealed class OrleansActorStream : IStream
{
    private readonly string _streamId;
    private readonly string _streamNamespace;
    private readonly global::Orleans.Streams.IStreamProvider _streamProvider;
    private readonly IStreamForwardingRegistry _forwardingRegistry;
    private readonly ILogger<OrleansActorStream> _logger;

    public OrleansActorStream(
        string streamId,
        string streamNamespace,
        global::Orleans.Streams.IStreamProvider streamProvider,
        IStreamForwardingRegistry? forwardingRegistry = null,
        ILogger<OrleansActorStream>? logger = null)
    {
        _streamId = streamId;
        _streamNamespace = streamNamespace;
        _streamProvider = streamProvider;
        _forwardingRegistry = forwardingRegistry ?? NoOpForwardingRegistry.Instance;
        _logger = logger ?? NullLogger<OrleansActorStream>.Instance;
    }

    public string StreamId => _streamId;

    public async Task ProduceAsync<T>(T message, CancellationToken ct = default)
        where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(message);
        ct.ThrowIfCancellationRequested();

        var envelope = message as EventEnvelope ?? new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            Route = new EnvelopeRoute
            {
                Direction = EventDirection.Down,
            },
        };

        await PublishToStreamAsync(_streamId, envelope);
        await RelayAsync(_streamId, envelope, ct);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
        where T : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(handler);

        var observer = new DelegateObserver<T>(handler);
        var handle = await ResolveStream().SubscribeAsync(observer);
        return new OrleansSubscriptionLease(handle);
    }

    public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ct.ThrowIfCancellationRequested();
        return _forwardingRegistry.UpsertAsync(CloneBindingForCurrentStream(binding), ct);
    }

    public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);
        ct.ThrowIfCancellationRequested();
        return _forwardingRegistry.RemoveAsync(_streamId, targetStreamId, ct);
    }

    public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _forwardingRegistry.ListBySourceAsync(_streamId, ct);
    }

    private IAsyncStream<EventEnvelope> ResolveStream()
    {
        var id = global::Orleans.Runtime.StreamId.Create(_streamNamespace, _streamId);
        return _streamProvider.GetStream<EventEnvelope>(id);
    }

    private IAsyncStream<EventEnvelope> ResolveStream(string targetStreamId)
    {
        var id = global::Orleans.Runtime.StreamId.Create(_streamNamespace, targetStreamId);
        return _streamProvider.GetStream<EventEnvelope>(id);
    }

    private Task PublishToStreamAsync(string targetStreamId, EventEnvelope envelope) =>
        ResolveStream(targetStreamId).OnNextAsync(envelope);

    private async Task RelayAsync(string sourceStreamId, EventEnvelope envelope, CancellationToken ct)
    {
        var queue = new Queue<(string SourceStreamId, EventEnvelope Envelope)>();
        var visitedSources = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((sourceStreamId, envelope));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (currentSourceId, currentEnvelope) = queue.Dequeue();
            if (!visitedSources.Add(currentSourceId))
                continue;

            var bindings = await _forwardingRegistry.ListBySourceAsync(currentSourceId, ct);
            foreach (var binding in bindings)
            {
                if (!StreamForwardingRules.TryBuildForwardedEnvelope(
                        currentSourceId,
                        binding,
                        currentEnvelope,
                        out var forwarded) ||
                    forwarded == null)
                {
                    continue;
                }

                queue.Enqueue((binding.TargetStreamId, forwarded));

                if (binding.ForwardingMode == StreamForwardingMode.TransitOnly)
                    continue;

                try
                {
                    await PublishToStreamAsync(binding.TargetStreamId, forwarded);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Orleans stream relay publish failed. source={SourceStreamId}, target={TargetStreamId}",
                        currentSourceId,
                        binding.TargetStreamId);
                }
            }
        }
    }

    private StreamForwardingBinding CloneBindingForCurrentStream(StreamForwardingBinding binding) =>
        new()
        {
            SourceStreamId = _streamId,
            TargetStreamId = binding.TargetStreamId,
            ForwardingMode = binding.ForwardingMode,
            DirectionFilter = new HashSet<EventDirection>(binding.DirectionFilter),
            EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
            Version = binding.Version,
            LeaseId = binding.LeaseId,
        };

    private sealed class OrleansSubscriptionLease : IAsyncDisposable
    {
        private readonly StreamSubscriptionHandle<EventEnvelope> _handle;
        private int _disposed;

        public OrleansSubscriptionLease(StreamSubscriptionHandle<EventEnvelope> handle)
        {
            _handle = handle;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            await _handle.UnsubscribeAsync();
        }
    }

    private sealed class NoOpForwardingRegistry : IStreamForwardingRegistry
    {
        public static NoOpForwardingRegistry Instance { get; } = new();

        public Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            _ = binding;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default)
        {
            _ = sourceStreamId;
            _ = targetStreamId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(string sourceStreamId, CancellationToken ct = default)
        {
            _ = sourceStreamId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }
    }

    private sealed class DelegateObserver<TMessage> : IAsyncObserver<EventEnvelope>
        where TMessage : IMessage, new()
    {
        private readonly Func<TMessage, Task> _handler;
        private readonly MessageDescriptor? _descriptor;

        public DelegateObserver(Func<TMessage, Task> handler)
        {
            _handler = handler;
            _descriptor = typeof(TMessage) == typeof(EventEnvelope) ? null : new TMessage().Descriptor;
        }

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex)
        {
            _ = ex;
            return Task.CompletedTask;
        }

        public async Task OnNextAsync(EventEnvelope item, StreamSequenceToken? token = null)
        {
            _ = token;

            if (typeof(TMessage) == typeof(EventEnvelope))
            {
                await ((Func<EventEnvelope, Task>)(object)_handler)(item);
                return;
            }

            if (item.Payload == null || _descriptor == null || !item.Payload.Is(_descriptor))
                return;

            await _handler(item.Payload.Unpack<TMessage>());
        }
    }
}
