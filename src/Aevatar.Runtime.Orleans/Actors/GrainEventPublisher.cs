// ─────────────────────────────────────────────────────────────
// GrainEventPublisher - Silo-side IEventPublisher.
// PublishAsync calls PropagateEventAsync directly (no self-loop).
// SendToAsync uses MassTransit Stream for async decoupling.
// ─────────────────────────────────────────────────────────────

using Aevatar.Orleans.Grains;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Orleans.Actors;

/// <summary>
/// Silo-side event publisher injected into Agent instances inside a Grain.
/// </summary>
internal sealed class GrainEventPublisher : IEventPublisher
{
    private readonly string _actorId;
    private readonly GAgentGrain _grain;
    private readonly IStreamProvider _streamProvider;
    private readonly ILogger _logger;

    /// <summary>Creates a Grain-side event publisher.</summary>
    public GrainEventPublisher(
        string actorId,
        GAgentGrain grain,
        IStreamProvider streamProvider,
        ILogger logger)
    {
        _actorId = actorId;
        _grain = grain;
        _streamProvider = streamProvider;
        _logger = logger;
    }

    /// <summary>
    /// Publishes event by calling PropagateEventAsync directly.
    /// Does NOT send to self stream — prevents self-loop:
    /// publish → stream → consumer → grain.HandleEvent → agent.HandleEvent (duplicate).
    /// </summary>
    public Task PublishAsync<TEvent>(
        TEvent evt,
        EventDirection direction = EventDirection.Down,
        CancellationToken ct = default) where TEvent : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = _actorId,
            Direction = direction,
        };

        // Fire-and-forget with fault logging
        _ = _grain.PropagateEventAsync(envelope)
            .ContinueWith(
                t => _logger.LogError(t.Exception,
                    "PropagateEventAsync failed for {ActorId}", _actorId),
                TaskContinuationOptions.OnlyOnFaulted);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends event to a specific target actor via MassTransit Stream (async decoupling).
    /// </summary>
    public async Task SendToAsync<TEvent>(
        string targetActorId,
        TEvent evt,
        CancellationToken ct = default) where TEvent : IMessage
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

        var targetStream = _streamProvider.GetStream(targetActorId);
        await targetStream.ProduceAsync(envelope, ct);
    }
}
