using Aevatar.Foundation.Abstractions;
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
    private const int DefaultSubscribeAttemptLimit = 30;
    private static readonly TimeSpan DefaultSubscribeRetryDelay = TimeSpan.FromSeconds(1);

    private readonly string _streamId;
    private readonly string _streamNamespace;
    private readonly global::Orleans.Streams.IStreamProvider _streamProvider;
    private readonly IStreamForwardingRegistry _forwardingRegistry;
    private readonly ILogger<OrleansActorStream> _logger;
    private readonly int _subscribeAttemptLimit;
    private readonly TimeSpan _subscribeRetryDelay;

    public OrleansActorStream(
        string streamId,
        string streamNamespace,
        global::Orleans.Streams.IStreamProvider streamProvider,
        IStreamForwardingRegistry? forwardingRegistry = null,
        ILogger<OrleansActorStream>? logger = null,
        int subscribeAttemptLimit = DefaultSubscribeAttemptLimit,
        TimeSpan? subscribeRetryDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(subscribeAttemptLimit, 1);

        var resolvedRetryDelay = subscribeRetryDelay ?? DefaultSubscribeRetryDelay;
        ArgumentOutOfRangeException.ThrowIfLessThan(resolvedRetryDelay, TimeSpan.Zero);

        _streamId = streamId;
        _streamNamespace = streamNamespace;
        _streamProvider = streamProvider;
        _forwardingRegistry = forwardingRegistry ?? NoOpForwardingRegistry.Instance;
        _logger = logger ?? NullLogger<OrleansActorStream>.Instance;
        _subscribeAttemptLimit = subscribeAttemptLimit;
        _subscribeRetryDelay = resolvedRetryDelay;
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

        try
        {
            await PublishToStreamAsync(_streamId, envelope);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EventPublicationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EventPublicationException(
                IsDefinitelyRejectedBeforeAdmission(ex)
                    ? EventPublicationFailureOutcome.NotAdmitted
                    : EventPublicationFailureOutcome.OutcomeUncertain,
                $"The Orleans stream '{_streamId}' failed while admitting the event.",
                ex);
        }

        try
        {
            await RelayAsync(_streamId, envelope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EventPublicationException(
                EventPublicationFailureOutcome.OutcomeUncertain,
                $"The event was admitted to Orleans stream '{_streamId}', but relay publication failed.",
                ex);
        }
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
        where T : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(handler);

        var observer = new DelegateObserver<T>(handler);
        for (var attempt = 1; attempt <= _subscribeAttemptLimit; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var handle = await ResolveStream().SubscribeAsync(observer);
                return new OrleansSubscriptionLease(handle);
            }
            catch (Exception ex) when (
                attempt < _subscribeAttemptLimit &&
                ex is OrleansMessageRejectionException or SiloUnavailableException)
            {
                if (attempt == 1 || attempt % 5 == 0)
                {
                    _logger.LogWarning(
                        ex,
                        "Orleans stream subscription was rejected during topology convergence. " +
                        "streamId={StreamId} attempt={Attempt}/{AttemptLimit}",
                        _streamId,
                        attempt,
                        _subscribeAttemptLimit);
                }

                await Task.Delay(_subscribeRetryDelay, ct);
            }
        }

        throw new InvalidOperationException("Orleans stream subscription retry loop exited unexpectedly.");
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

    private static bool IsDefinitelyRejectedBeforeAdmission(Exception exception) =>
        exception switch
        {
            OrleansMessageRejectionException => true,
            AggregateException aggregate when aggregate.InnerExceptions.Count > 0 =>
                aggregate.InnerExceptions.All(IsDefinitelyRejectedBeforeAdmission),
            _ => false,
        };

    private async Task RelayAsync(string sourceStreamId, EventEnvelope envelope, CancellationToken ct)
    {
        var queue = new Queue<(string SourceStreamId, EventEnvelope Envelope)>();
        var visitedSources = new HashSet<string>(StringComparer.Ordinal);
        List<Exception>? publishFailures = null;
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    publishFailures ??= [];
                    publishFailures.Add(ex);
                    _logger.LogWarning(
                        ex,
                        "Orleans stream relay publish failed. source={SourceStreamId}, target={TargetStreamId}",
                        currentSourceId,
                        binding.TargetStreamId);
                }
            }
        }

        if (publishFailures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more Orleans stream relay publishes failed.",
                publishFailures);
        }
    }

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
            TargetActorKind = binding.TargetActorKind,
            ActivationGeneration = binding.ActivationGeneration,
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
