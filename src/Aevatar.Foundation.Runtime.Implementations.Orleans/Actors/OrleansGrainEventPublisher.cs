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
    private readonly Func<EventEnvelope, Task> _dispatchToSelfAsync;
    private readonly IEnvelopePropagationPolicy _propagationPolicy;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;

    public OrleansGrainEventPublisher(
        string actorId,
        Func<string?> getParentId,
        Func<EventEnvelope, Task> dispatchToSelfAsync,
        IEnvelopePropagationPolicy propagationPolicy,
        Aevatar.Foundation.Abstractions.IStreamProvider streams)
    {
        _actorId = actorId;
        _getParentId = getParentId;
        _dispatchToSelfAsync = dispatchToSelfAsync;
        _propagationPolicy = propagationPolicy;
        _streams = streams;
    }

    public async Task PublishAsync<TEvent>(
        TEvent evt,
        EventDirection direction = EventDirection.Down,
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
            Route = new EnvelopeRoute
            {
                PublisherActorId = _actorId,
                Direction = direction,
            },
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
            case EventDirection.Self:
                await DispatchAsync(_actorId, _actorId, envelope, ct);
                break;
            case EventDirection.Down:
                await _streams.GetStream(_actorId).ProduceAsync(envelope, ct);
                break;
            case EventDirection.Up:
            {
                var parentId = _getParentId();
                if (!string.IsNullOrWhiteSpace(parentId))
                    await DispatchAsync(_actorId, parentId, envelope, ct);
                break;
            }
            case EventDirection.Both:
            {
                var tasks = new List<Task>
                {
                    _streams.GetStream(_actorId).ProduceAsync(envelope, ct),
                };
                var parentId = _getParentId();
                if (!string.IsNullOrWhiteSpace(parentId))
                    tasks.Add(DispatchAsync(_actorId, parentId, envelope, ct));
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
            Route = new EnvelopeRoute
            {
                PublisherActorId = _actorId,
                Direction = EventDirection.Self,
                TargetActorId = targetActorId,
            },
        };
        EnvelopePublishContextHelpers.ApplyOutboundPublishContext(
            envelope,
            sourceEnvelope,
            _propagationPolicy,
            _actorId,
            routeTargetCount: 1,
            options);
        return DispatchAsync(_actorId, targetActorId, envelope, ct);
    }

    private long? EstimateRouteTargetCount(EventDirection direction) =>
        direction switch
        {
            EventDirection.Self => 1,
            EventDirection.Up => string.IsNullOrWhiteSpace(_getParentId()) ? 0 : 1,
            // Down/Both fan-out count is stream-subscriber dependent and unknown at publish time.
            _ => null,
        };

    private Task DispatchAsync(string senderActorId, string targetActorId, EventEnvelope envelope, CancellationToken ct)
    {
        var routedEnvelope = envelope.Clone();
        VisitedActorChain.AppendDispatchPublisher(routedEnvelope, senderActorId, targetActorId);

        if (string.Equals(targetActorId, _actorId, StringComparison.Ordinal))
            return _dispatchToSelfAsync(routedEnvelope);

        return _streams.GetStream(targetActorId).ProduceAsync(routedEnvelope, ct);
    }
}
