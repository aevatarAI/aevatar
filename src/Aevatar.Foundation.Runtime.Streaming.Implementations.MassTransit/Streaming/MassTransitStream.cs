using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

internal sealed class MassTransitStream : IStream
{
    private readonly string _streamId;
    private readonly string _streamNamespace;
    private readonly IMassTransitEnvelopeTransport _transport;
    private readonly IStreamForwardingRegistry _forwardingRegistry;
    private readonly ILogger<MassTransitStream> _logger;

    public MassTransitStream(
        string streamId,
        string streamNamespace,
        IMassTransitEnvelopeTransport transport,
        IStreamForwardingRegistry? forwardingRegistry = null,
        ILogger<MassTransitStream>? logger = null)
    {
        _streamId = streamId;
        _streamNamespace = streamNamespace;
        _transport = transport;
        _forwardingRegistry = forwardingRegistry ?? NoOpForwardingRegistry.Instance;
        _logger = logger ?? NullLogger<MassTransitStream>.Instance;
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
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(string.Empty, TopologyAudience.Children),
        };

        await PublishToStreamAsync(_streamId, envelope, ct);
        await RelayAsync(_streamId, envelope, ct);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
        where T : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(handler);

        var descriptor = typeof(T) == typeof(EventEnvelope) ? null : new T().Descriptor;
        return await _transport.SubscribeAsync(async record =>
        {
            if (!string.Equals(record.StreamNamespace, _streamNamespace, StringComparison.Ordinal) ||
                !string.Equals(record.StreamId, _streamId, StringComparison.Ordinal) ||
                record.Payload is not { Length: > 0 })
            {
                return;
            }

            EventEnvelope envelope;
            try
            {
                envelope = EventEnvelope.Parser.ParseFrom(record.Payload);
            }
            catch
            {
                return;
            }

            if (typeof(T) == typeof(EventEnvelope))
            {
                await ((Func<EventEnvelope, Task>)(object)handler)(envelope);
                return;
            }

            if (envelope.Payload == null || descriptor == null || !envelope.Payload.Is(descriptor))
                return;

            await handler(envelope.Payload.Unpack<T>());
        }, ct);
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
                    await PublishToStreamAsync(binding.TargetStreamId, forwarded, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "MassTransit stream relay publish failed. source={SourceStreamId}, target={TargetStreamId}",
                        currentSourceId,
                        binding.TargetStreamId);
                }
            }
        }
    }

    private Task PublishToStreamAsync(string targetStreamId, EventEnvelope envelope, CancellationToken ct) =>
        _transport.PublishAsync(_streamNamespace, targetStreamId, envelope.ToByteArray(), ct);

    private StreamForwardingBinding CloneBindingForCurrentStream(StreamForwardingBinding binding) =>
        new()
        {
            SourceStreamId = _streamId,
            TargetStreamId = binding.TargetStreamId,
            ForwardingMode = binding.ForwardingMode,
            DirectionFilter = new HashSet<TopologyAudience>(binding.DirectionFilter),
            EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
            Version = binding.Version,
            LeaseId = binding.LeaseId,
        };

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
}
