using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

internal sealed class OrleansGrainEventPublisher : IEventPublisher
{
    private readonly string _actorId;
    private readonly IGrainFactory _grainFactory;
    private readonly Func<string?> _getParentId;
    private readonly Func<EventEnvelope, Task> _dispatchToSelfAsync;
    private readonly IEnvelopePropagationPolicy _propagationPolicy;
    private readonly IStreamForwardingRegistry _forwardingRegistry;

    public OrleansGrainEventPublisher(
        string actorId,
        IGrainFactory grainFactory,
        Func<string?> getParentId,
        Func<EventEnvelope, Task> dispatchToSelfAsync,
        IEnvelopePropagationPolicy propagationPolicy,
        IStreamForwardingRegistry forwardingRegistry)
    {
        _actorId = actorId;
        _grainFactory = grainFactory;
        _getParentId = getParentId;
        _dispatchToSelfAsync = dispatchToSelfAsync;
        _propagationPolicy = propagationPolicy;
        _forwardingRegistry = forwardingRegistry;
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
                await DispatchAsync(_actorId, _actorId, envelope);
                break;
            case EventDirection.Down:
                await DispatchDownAsync(envelope, ct);
                break;
            case EventDirection.Up:
            {
                var parentId = _getParentId();
                if (!string.IsNullOrWhiteSpace(parentId))
                    await DispatchAsync(_actorId, parentId, envelope);
                break;
            }
            case EventDirection.Both:
            {
                var tasks = new List<Task>
                {
                    DispatchDownAsync(envelope, ct),
                };
                var parentId = _getParentId();
                if (!string.IsNullOrWhiteSpace(parentId))
                    tasks.Add(DispatchAsync(_actorId, parentId, envelope));
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
        return DispatchAsync(_actorId, targetActorId, envelope);
    }

    private async Task DispatchDownAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var queue = new Queue<(string SourceActorId, EventEnvelope Envelope)>();
        var visitedSources = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue((_actorId, envelope));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (sourceActorId, currentEnvelope) = queue.Dequeue();
            if (!visitedSources.Add(sourceActorId))
                continue;

            var bindings = await _forwardingRegistry.ListBySourceAsync(sourceActorId, ct);
            List<Task>? dispatchTasks = null;

            foreach (var binding in bindings)
            {
                if (!StreamForwardingRules.TryBuildForwardedEnvelope(
                        sourceActorId,
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

                dispatchTasks ??= new List<Task>();
                dispatchTasks.Add(DispatchAsync(sourceActorId, binding.TargetStreamId, forwarded));
            }

            if (dispatchTasks is { Count: > 0 })
            {
                await Task.WhenAll(dispatchTasks);
            }
        }
    }

    private Task DispatchAsync(string senderActorId, string targetActorId, EventEnvelope envelope)
    {
        var routedEnvelope = envelope.Clone();
        PublisherChainMetadata.AppendDispatchPublisher(routedEnvelope, senderActorId, targetActorId);

        if (string.Equals(targetActorId, _actorId, StringComparison.Ordinal))
            return _dispatchToSelfAsync(routedEnvelope);

        var grain = _grainFactory.GetGrain<IRuntimeActorGrain>(targetActorId);
        return grain.HandleEnvelopeAsync(routedEnvelope.ToByteArray());
    }
}
