namespace Aevatar.Foundation.Abstractions.Runtime.Callbacks;

public sealed class RuntimeCallbackTimeoutRequest
{
    public required string ActorId { get; init; }

    public required string CallbackId { get; init; }

    public required EventEnvelope TriggerEnvelope { get; init; }

    public required TimeSpan DueTime { get; init; }

    public RuntimeCallbackDeliveryMode DeliveryMode { get; init; } = RuntimeCallbackDeliveryMode.FiredSelfEvent;
}
