// LocalActorPublisher - IEventPublisher routing implementation.
// Routes events to the correct stream based on direction.

using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Runtime.Propagation;
using Aevatar.Foundation.Runtime.Routing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Implementations.Local.Actors;

/// <summary>Local runtime publisher for topology delivery, direct delivery, and committed-state observation.</summary>
public sealed class LocalActorPublisher : IEventPublisher, ICommittedStateEventPublisher
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
        TopologyAudience audience = TopologyAudience.Children,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null)
        where TEvent : IMessage
    {
        var routeTargetCount = GetRouteTargetCount(audience);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(_actorId, audience),
        };
        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            envelope,
            sourceEnvelope,
            _envelopePropagationPolicy,
            _actorId,
            routeTargetCount,
            options);

        switch (audience)
        {
            case TopologyAudience.Self:
                await _selfStream.ProduceAsync(envelope, ct);
                break;
            case TopologyAudience.Children:
                await _selfStream.ProduceAsync(envelope, ct);
                break;
            case TopologyAudience.Parent:
                if (_router.ParentId != null)
                    await _streams.GetStream(_router.ParentId).ProduceAsync(envelope, ct);
                break;
            case TopologyAudience.ParentAndChildren:
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

    async Task ICommittedStateEventPublisher.PublishAsync(
        CommittedStateEventPublished evt,
        ObserverAudience audience,
        CancellationToken ct,
        EventEnvelope? sourceEnvelope,
        EventEnvelopePublishOptions? options)
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(_actorId, audience),
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

    private long GetRouteTargetCount(TopologyAudience audience) =>
        audience switch
        {
            TopologyAudience.Self => 1,
            TopologyAudience.Children => _router.ChildrenIds.Count,
            TopologyAudience.Parent => _router.ParentId != null ? 1 : 0,
            TopologyAudience.ParentAndChildren => _router.ChildrenIds.Count + (_router.ParentId != null ? 1 : 0),
            _ => 0,
        };

}
