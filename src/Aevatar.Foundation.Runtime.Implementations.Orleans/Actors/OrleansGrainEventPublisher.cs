using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Propagation;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

internal sealed class OrleansGrainEventPublisher : IEventPublisher
{
    private readonly string _actorId;
    private readonly Func<string?> _getParentId;
    private readonly IEnvelopePropagationPolicy _propagationPolicy;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;

    public OrleansGrainEventPublisher(
        string actorId,
        Func<string?> getParentId,
        IEnvelopePropagationPolicy propagationPolicy,
        Aevatar.Foundation.Abstractions.IStreamProvider streams)
    {
        _actorId = actorId;
        _getParentId = getParentId;
        _propagationPolicy = propagationPolicy;
        _streams = streams;
    }

    public async Task PublishAsync<TEvent>(
        TEvent evt,
        BroadcastDirection direction = BroadcastDirection.Down,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateBroadcast(_actorId, direction),
        };
        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            envelope,
            sourceEnvelope,
            _propagationPolicy,
            _actorId,
            EstimateRouteTargetCount(direction),
            options);

        switch (direction)
        {
            case BroadcastDirection.Self:
                await _streams.GetStream(_actorId).ProduceAsync(envelope, ct);
                break;
            case BroadcastDirection.Down:
                await _streams.GetStream(_actorId).ProduceAsync(envelope, ct);
                break;
            case BroadcastDirection.Up:
            {
                var parentId = _getParentId();
                if (!string.IsNullOrWhiteSpace(parentId))
                    await DispatchAsync(parentId, envelope, ct);
                break;
            }
            case BroadcastDirection.Both:
            {
                var tasks = new List<Task>
                {
                    _streams.GetStream(_actorId).ProduceAsync(envelope, ct),
                };
                var parentId = _getParentId();
                if (!string.IsNullOrWhiteSpace(parentId))
                    tasks.Add(DispatchAsync(parentId, envelope, ct));
                await Task.WhenAll(tasks);
                break;
            }
        }
    }

    public Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateDirect(_actorId, targetActorId),
        };
        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            envelope,
            sourceEnvelope,
            _propagationPolicy,
            _actorId,
            routeTargetCount: 1,
            options);
        if (string.Equals(targetActorId, _actorId, StringComparison.Ordinal))
            return _streams.GetStream(_actorId).ProduceAsync(envelope, ct);

        return DispatchAsync(targetActorId, envelope, ct);
    }

    public Task PublishCommittedAsync<TEvent>(
        TEvent evt,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateObserve(_actorId),
        };
        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            envelope,
            sourceEnvelope,
            _propagationPolicy,
            _actorId,
            routeTargetCount: 0,
            options);
        return _streams.GetStream(_actorId).ProduceAsync(envelope, ct);
    }

    private long? EstimateRouteTargetCount(BroadcastDirection direction) =>
        direction switch
        {
            BroadcastDirection.Self => 1,
            BroadcastDirection.Up => string.IsNullOrWhiteSpace(_getParentId()) ? 0 : 1,
            // Down/Both fan-out count is stream-subscriber dependent and unknown at publish time.
            _ => null,
        };

    private Task DispatchAsync(string targetActorId, EventEnvelope envelope, CancellationToken ct)
    {
        var routedEnvelope = envelope.Clone();
        VisitedActorChain.AppendDispatchPublisher(routedEnvelope, _actorId, targetActorId);

        return _streams.GetStream(targetActorId).ProduceAsync(routedEnvelope, ct);
    }
}
