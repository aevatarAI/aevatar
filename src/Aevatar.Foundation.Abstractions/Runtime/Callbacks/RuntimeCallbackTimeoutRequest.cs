namespace Aevatar.Foundation.Abstractions.Runtime.Callbacks;

public sealed class RuntimeCallbackTimeoutRequest
{
    public required string ActorId { get; init; }

    public required string CallbackId { get; init; }

    public required EventEnvelope TriggerEnvelope { get; init; }

    public required TimeSpan DueTime { get; init; }

    public RuntimeCallbackDeliveryMode DeliveryMode { get; init; } = RuntimeCallbackDeliveryMode.FiredSelfEvent;

    /// <summary>
    /// Optional authoritative cursor for a one-shot callback where only the latest pending
    /// sequence for the same key remains relevant.
    /// </summary>
    public RuntimeEnvelopeRetryCoalescingCursor? CoalescingCursor { get; init; }
}
