namespace Aevatar.Foundation.Abstractions.Runtime.Async;

public sealed class RuntimeTimerRequest
{
    public required string ActorId { get; init; }

    public required string CallbackId { get; init; }

    public required EventEnvelope TriggerEnvelope { get; init; }

    public required TimeSpan DueTime { get; init; }

    public required TimeSpan Period { get; init; }
}
