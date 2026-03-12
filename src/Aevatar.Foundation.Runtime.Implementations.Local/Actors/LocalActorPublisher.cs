// LocalActorPublisher - IEventPublisher routing implementation.
// Routes events to the correct stream based on direction.

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Runtime.Propagation;
using Aevatar.Foundation.Runtime.Routing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

/// <summary>IEventPublisher that routes events to streams using BroadcastDirection.</summary>
public sealed class LocalActorPublisher : IEventPublisher
{
    private readonly string _actorId;
    private readonly EventRouter _router;
    private readonly IStreamProvider _streams;
    private readonly IStream _selfStream;
    private readonly IEnvelopePropagationPolicy _envelopePropagationPolicy;

    public LocalActorPublisher(
        string actorId,
        EventRouter router,
        IStreamProvider streams,
        IEnvelopePropagationPolicy? envelopePropagationPolicy = null)
    {
        _actorId = actorId;
        _router = router;
        _streams = streams;
        _selfStream = streams.GetStream(actorId);
        _envelopePropagationPolicy = envelopePropagationPolicy
            ?? new DefaultEnvelopePropagationPolicy(new DefaultCorrelationLinkPolicy());
    }

    public async Task PublishAsync<TEvent>(
        TEvent evt,
        BroadcastDirection direction = BroadcastDirection.Down,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        var routeTargetCount = GetRouteTargetCount(direction);
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
            _envelopePropagationPolicy,
            _actorId,
            routeTargetCount,
            options);

        switch (direction)
        {
            case BroadcastDirection.Self:
                await _selfStream.ProduceAsync(envelope, ct);
                break;
            case BroadcastDirection.Down:
                await _selfStream.ProduceAsync(envelope, ct);
                break;
            case BroadcastDirection.Up:
                if (_router.ParentId != null)
                    await _streams.GetStream(_router.ParentId).ProduceAsync(envelope, ct);
                break;
            case BroadcastDirection.Both:
                await _selfStream.ProduceAsync(envelope, ct);
                if (_router.ParentId != null)
                    await _streams.GetStream(_router.ParentId).ProduceAsync(envelope, ct);
                break;
        }
    }

    public async Task SendToAsync<TEvent>(
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
            _envelopePropagationPolicy,
            _actorId,
            routeTargetCount: 1,
            options);
        await _streams.GetStream(targetActorId).ProduceAsync(envelope, ct);
    }

    public async Task PublishCommittedAsync<TEvent>(
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
            _envelopePropagationPolicy,
            _actorId,
            routeTargetCount: 0,
            options);
        await _selfStream.ProduceAsync(envelope, ct);
    }

    private long GetRouteTargetCount(BroadcastDirection direction) =>
        direction switch
        {
            BroadcastDirection.Self => 1,
            BroadcastDirection.Down => _router.ChildrenIds.Count,
            BroadcastDirection.Up => _router.ParentId != null ? 1 : 0,
            BroadcastDirection.Both => _router.ChildrenIds.Count + (_router.ParentId != null ? 1 : 0),
            _ => 0,
        };

}
