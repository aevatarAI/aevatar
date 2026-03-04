// LocalActorPublisher - IEventPublisher routing implementation.
// Routes events to the correct stream based on direction.

using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Runtime.Propagation;
using Aevatar.Foundation.Runtime.Routing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Actors;

/// <summary>IEventPublisher that routes events to streams using EventDirection.</summary>
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
        EventDirection direction = EventDirection.Down,
        CancellationToken ct = default,
        EventEnvelope? sourceEnvelope = null)
        where TEvent : IMessage
    {
        var routeTargetCount = GetRouteTargetCount(direction);
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = _actorId,
            Direction = direction,
        };
        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            envelope,
            sourceEnvelope,
            _envelopePropagationPolicy,
            _actorId,
            routeTargetCount);

        switch (direction)
        {
            case EventDirection.Self:
                await _selfStream.ProduceAsync(envelope, ct);
                break;
            case EventDirection.Down:
                await _selfStream.ProduceAsync(envelope, ct);
                break;
            case EventDirection.Up:
                if (_router.ParentId != null)
                    await _streams.GetStream(_router.ParentId).ProduceAsync(envelope, ct);
                break;
            case EventDirection.Both:
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
        EventEnvelope? sourceEnvelope = null)
        where TEvent : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = _actorId,
            Direction = EventDirection.Self, // Point-to-point: target handles it as Self
            TargetActorId = targetActorId,
        };
        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            envelope,
            sourceEnvelope,
            _envelopePropagationPolicy,
            _actorId,
            routeTargetCount: 1);
        await _streams.GetStream(targetActorId).ProduceAsync(envelope, ct);
    }

    private long GetRouteTargetCount(EventDirection direction) =>
        direction switch
        {
            EventDirection.Self => 1,
            EventDirection.Down => _router.ChildrenIds.Count,
            EventDirection.Up => _router.ParentId != null ? 1 : 0,
            EventDirection.Both => _router.ChildrenIds.Count + (_router.ParentId != null ? 1 : 0),
            _ => 0,
        };

}
