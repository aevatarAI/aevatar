namespace Aevatar.Foundation.Abstractions.Runtime;

/// <summary>
/// Actor-facing owner operation for the one runtime-reserved fleet reconcile
/// callback. Callers cannot choose its identity, payload, or cadence.
/// </summary>
public interface IRuntimeFleetReconcileScheduleOwner
{
    Task EnsureScheduledAsync(CancellationToken ct = default);

    /// <summary>
    /// Confirms that the authority committed the exact runtime-attested delivery. Until this
    /// acknowledgement reaches the scheduler, the reserved slot keeps re-publishing the same
    /// envelope instead of advancing to a moving latest delivery.
    /// </summary>
    Task AcknowledgeDeliveryAsync(
        RuntimeFleetReconcileDeliveryAttestation attestation,
        CancellationToken ct = default);
}

/// <summary>
/// Runtime-ingress verifier for the exact callback delivery persisted by the
/// scheduler. This port must never be queried from an actor event handler.
/// </summary>
public interface IRuntimeFleetReconcileDeliveryVerifier
{
    Task<RuntimeFleetReconcileDeliveryAttestation?> VerifyAsync(
        EventEnvelope envelope,
        CancellationToken ct = default);
}

/// <summary>
/// Read-only actor-turn view bound by runtime ingress after verification.
/// The attestation is process context, not a serializable message contract.
/// </summary>
public interface IRuntimeFleetReconcileDeliveryAttestationReader
{
    RuntimeFleetReconcileDeliveryAttestation? Current { get; }
}

public sealed record RuntimeFleetReconcileDeliveryAttestation(
    string EnvelopeId,
    long Generation,
    long FireIndex,
    int SlotEpoch);
