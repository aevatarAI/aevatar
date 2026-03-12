// ─────────────────────────────────────────────────────────────
// IEventPublisher - event publishing contract.
// Supports directional broadcast and point-to-point delivery.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Event publishing contract for actor delivery and committed-event observation.
/// Publish/send never imply inline execution; `PublishAsync` produces a broadcast route, `SendToAsync` produces a direct route.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Publishes an event using the specified broadcast route.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task PublishAsync<TEvent>(TEvent evt, BroadcastDirection direction = BroadcastDirection.Down,
        CancellationToken ct = default, EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null) where TEvent : IMessage;

    /// <summary>Sends an event to the target actor's inbox.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default, EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null) where TEvent : IMessage;

    /// <summary>Publishes a committed event for observation-only consumers.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task PublishCommittedAsync<TEvent>(TEvent evt,
        CancellationToken ct = default, EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null) where TEvent : IMessage =>
        throw new NotSupportedException($"{GetType().Name} does not support observation publishing.");
}
