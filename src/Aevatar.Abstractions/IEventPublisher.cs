// ─────────────────────────────────────────────────────────────
// IEventPublisher - event publishing contract.
// Supports directional broadcast and point-to-point delivery.
// ─────────────────────────────────────────────────────────────

using Google.Protobuf;

namespace Aevatar;

/// <summary>
/// Event publishing contract for stream broadcast or direct actor delivery.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Publishes an event using the specified direction (Up/Down/Both).</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down,
        CancellationToken ct = default) where TEvent : IMessage;

    /// <summary>Sends an event directly to a target actor.</summary>
    /// <typeparam name="TEvent">Event type, must implement Protobuf IMessage.</typeparam>
    Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default) where TEvent : IMessage;
}
