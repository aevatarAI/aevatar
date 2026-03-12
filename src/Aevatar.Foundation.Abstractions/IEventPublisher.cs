// ─────────────────────────────────────────────────────────────
// IEventPublisher - event publishing contract.
// Supports directional broadcast and point-to-point delivery.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Event publishing contract for actor delivery and observer-only committed-event publication.
/// Publish/send never imply inline execution; `EventDirection.Observe` writes an observer-visible envelope that actor inbox handlers ignore.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Publishes an event using the specified route (Up/Down/Both/Self/Observe).</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down,
        CancellationToken ct = default, EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null) where TEvent : IMessage;

    /// <summary>Sends an event to the target actor's inbox.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default, EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null) where TEvent : IMessage;
}
