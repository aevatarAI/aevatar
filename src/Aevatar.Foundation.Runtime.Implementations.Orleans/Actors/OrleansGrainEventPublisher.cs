using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Aevatar.Foundation.Abstractions.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;

internal sealed class OrleansGrainEventPublisher : IEventPublisher
{
    private readonly string _actorId;
    private readonly Func<string?> _getParentId;
    private readonly Func<EventEnvelope, Task> _dispatchToSelfAsync;
    private readonly IEnvelopePropagationPolicy _propagationPolicy;
    private readonly Aevatar.Foundation.Abstractions.IStreamProvider _streams;
    private readonly ILogger _logger;

    public OrleansGrainEventPublisher(
        string actorId,
        Func<string?> getParentId,
        Func<EventEnvelope, Task> dispatchToSelfAsync,
        IEnvelopePropagationPolicy propagationPolicy,
        Aevatar.Foundation.Abstractions.IStreamProvider streams,
        ILogger? logger = null)
    {
        _actorId = actorId;
        _getParentId = getParentId;
        _dispatchToSelfAsync = dispatchToSelfAsync;
        _propagationPolicy = propagationPolicy;
        _streams = streams;
        _logger = logger ?? NullLogger.Instance;
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
        envelope.Metadata["__source_actor_id"] = _actorId;

        _propagationPolicy.Apply(envelope, sourceEnvelope);

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
                {
                    _logger.LogDebug(
                        "PublishAsync(Up): actor={ActorId} → parent={ParentId} type={Type} envelope={EnvelopeId}",
                        _actorId, parentId, envelope.Payload?.TypeUrl, envelope.Id);
                    await DispatchAsync(_actorId, parentId, envelope, ct);
                }
                else
                {
                    _logger.LogWarning(
                        "PublishAsync(Up): actor={ActorId} has NO parent, dropping type={Type} envelope={EnvelopeId}",
                        _actorId, envelope.Payload?.TypeUrl, envelope.Id);
                }
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
        envelope.Metadata["__source_actor_id"] = _actorId;

        _propagationPolicy.Apply(envelope, sourceEnvelope);
        return DispatchAsync(_actorId, targetActorId, envelope, ct);
    }

    private Task DispatchAsync(string senderActorId, string targetActorId, EventEnvelope envelope, CancellationToken ct)
    {
        var routedEnvelope = envelope.Clone();
        PublisherChainMetadata.AppendDispatchPublisher(routedEnvelope, senderActorId, targetActorId);

        if (string.Equals(targetActorId, _actorId, StringComparison.Ordinal))
            return _dispatchToSelfAsync(routedEnvelope);

        return _streams.GetStream(targetActorId).ProduceAsync(routedEnvelope, ct);
    }
}
