using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Propagation;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

internal sealed class OrleansGrainEventPublisher : IEventPublisher
{
    private readonly string _actorId;
    private readonly IGrainFactory _grainFactory;
    private readonly Func<string?> _getParentId;
    private readonly Func<IReadOnlyList<string>> _getChildrenIds;
    private readonly Func<EventEnvelope, Task> _dispatchToSelfAsync;
    private readonly IEnvelopePropagationPolicy _propagationPolicy;
    private readonly IEventLoopGuard _loopGuard;

    public OrleansGrainEventPublisher(
        string actorId,
        IGrainFactory grainFactory,
        Func<string?> getParentId,
        Func<IReadOnlyList<string>> getChildrenIds,
        Func<EventEnvelope, Task> dispatchToSelfAsync,
        IEnvelopePropagationPolicy propagationPolicy,
        IEventLoopGuard loopGuard)
    {
        _actorId = actorId;
        _grainFactory = grainFactory;
        _getParentId = getParentId;
        _getChildrenIds = getChildrenIds;
        _dispatchToSelfAsync = dispatchToSelfAsync;
        _propagationPolicy = propagationPolicy;
        _loopGuard = loopGuard;
    }

    public async Task PublishAsync<TEvent>(
        TEvent evt,
        EventDirection direction = EventDirection.Down,
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
            Direction = direction,
        };

        _propagationPolicy.Apply(envelope, sourceEnvelope);

        switch (direction)
        {
            case EventDirection.Self:
                await DispatchAsync(_actorId, envelope);
                break;
            case EventDirection.Down:
                await DispatchManyAsync(_getChildrenIds(), envelope);
                break;
            case EventDirection.Up:
            {
                var parentId = _getParentId();
                if (!string.IsNullOrWhiteSpace(parentId))
                    await DispatchAsync(parentId, envelope);
                break;
            }
            case EventDirection.Both:
            {
                var tasks = _getChildrenIds().Select(childId => DispatchAsync(childId, envelope)).ToList();
                var parentId = _getParentId();
                if (!string.IsNullOrWhiteSpace(parentId))
                    tasks.Add(DispatchAsync(parentId, envelope));
                await Task.WhenAll(tasks);
                break;
            }
        }
    }

    public Task SendToAsync<TEvent>(
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
            Direction = EventDirection.Self,
            TargetActorId = targetActorId,
        };

        _propagationPolicy.Apply(envelope, sourceEnvelope);
        return DispatchAsync(targetActorId, envelope);
    }

    private Task DispatchManyAsync(IReadOnlyList<string> actorIds, EventEnvelope envelope)
    {
        if (actorIds.Count == 0)
            return Task.CompletedTask;

        return Task.WhenAll(actorIds.Select(actorId => DispatchAsync(actorId, envelope)));
    }

    private Task DispatchAsync(string targetActorId, EventEnvelope envelope)
    {
        var routedEnvelope = envelope.Clone();
        _loopGuard.BeforeDispatch(_actorId, targetActorId, routedEnvelope);

        if (string.Equals(targetActorId, _actorId, StringComparison.Ordinal))
            return _dispatchToSelfAsync(routedEnvelope);

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(targetActorId);
        return grain.HandleEnvelopeAsync(routedEnvelope.ToByteArray());
    }
}
