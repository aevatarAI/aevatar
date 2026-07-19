namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Runtime-neutral admission receipt for an envelope accepted by an actor runtime/inbox boundary.
/// </summary>
public sealed record DispatchAdmission(
    bool Accepted,
    string CommandId,
    DateTimeOffset AckedAt,
    string ActorId,
    string CorrelationId);

public sealed class ActorNotFoundException : InvalidOperationException
{
    public ActorNotFoundException(string actorId)
        : base($"Actor '{actorId}' was not found.")
    {
        ActorId = actorId;
    }

    public string ActorId { get; }
}

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
/// Actor envelope dispatch contract.
/// </summary>
// Refactor (iter149/issue1132): Old pattern: handled-dispatch side contract implied actor-turn completion.  New principle: IActorDispatchPort exposes accepted-only runtime/inbox admission.
public interface IActorDispatchPort
{
    /// <summary>
    /// Admits an envelope to the specified actor runtime/inbox boundary.
    /// Completion only means accepted-for-dispatch with a stable command id; it does not mean handled,
    /// committed, or observed by a read model.
    /// </summary>
    Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default);
}
