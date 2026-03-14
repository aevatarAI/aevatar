// ─────────────────────────────────────────────────────────────
// IEventPublisher - event publishing contract.
// Supports directional broadcast and point-to-point delivery.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Event publishing contract for the unified message plane.
/// Publish/send never imply inline execution; `PublishAsync` produces a topology publication route,
/// and `SendToAsync` produces a direct route.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Publishes an event using the specified topology publication route.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task PublishAsync<TEvent>(TEvent evt, TopologyAudience audience = TopologyAudience.Children,
        CancellationToken ct = default, EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null) where TEvent : IMessage;

    /// <summary>Sends an event to the target actor's inbox.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default, EventEnvelope? sourceEnvelope = null,
        EventEnvelopePublishOptions? options = null) where TEvent : IMessage;

}
