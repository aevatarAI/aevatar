namespace Aevatar.Foundation.Abstractions;

public enum DispatchAdmissionFollowUpStage
{
    Unspecified = 0,
    Handled = 1,
    Committed = 2,
    ReadModelObserved = 3,
}

/// <summary>
/// Runtime-neutral admission receipt for an envelope accepted by an actor runtime/inbox boundary.
/// </summary>
public sealed record DispatchAdmission(
    bool Accepted,
    string CommandId,
    DateTimeOffset AckedAt,
    string ActorId,
    string CorrelationId);

public static class DispatchAdmissionFactory
{
    public static DispatchAdmission Create(string actorId, EventEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(envelope);

        var commandId = string.IsNullOrWhiteSpace(envelope.Id)
            ? Guid.NewGuid().ToString("N")
            : envelope.Id.Trim();
        var correlationId = envelope.Propagation?.CorrelationId;
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = commandId;

        return new DispatchAdmission(
            true,
            commandId,
            DateTimeOffset.UtcNow,
            actorId.Trim(),
            correlationId.Trim());
    }
}

/// <summary>
/// Optional observation event for phases that happen after dispatch admission.
/// </summary>
public sealed record DispatchAdmissionFollowUp(
    string CommandId,
    string ActorId,
    DispatchAdmissionFollowUpStage Stage,
    DateTimeOffset ObservedAt,
    string? CorrelationId = null,
    long? StateVersion = null);

/// <summary>
/// Actor envelope dispatch contract.
/// </summary>
public interface IActorDispatchPort
{
    /// <summary>
    /// Admits an envelope to the specified actor runtime/inbox boundary.
    /// Completion only means accepted-for-dispatch with a stable command id; it does not mean handled,
    /// committed, or observed by a read model.
    /// </summary>
    Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default);
}
