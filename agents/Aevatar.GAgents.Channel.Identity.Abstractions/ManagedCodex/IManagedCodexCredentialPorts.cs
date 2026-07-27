using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Read-only current-state projection query for one NyxID user's managed Codex
/// invocation credential.
/// </summary>
public interface IManagedCodexCredentialQueryPort
{
    /// <summary>Returns the current projected credential facts, or null when none have been committed.</summary>
    Task<ManagedCodexCredentialSnapshot?> ResolveAsync(
        ExternalSubjectRef owner,
        CancellationToken ct = default);
}

/// <summary>
/// Binds an owner-scoped observation to readiness-capable committed managed
/// Codex credential snapshots emitted by the unified Projection Pipeline.
/// </summary>
public interface IManagedCodexCredentialReadinessObservationPort
{
    /// <summary>
    /// Creates a fresh observation session that emits only committed provision,
    /// rotation, policy-reconciliation, or explicit readiness-confirmation facts.
    /// </summary>
    Task<IManagedCodexCredentialReadinessObservationLease> BindAsync(
        ExternalSubjectRef owner,
        CancellationToken ct = default);
}

/// <summary>
/// Reads committed managed Codex credential snapshots for one observation
/// session.
/// </summary>
public interface IManagedCodexCredentialReadinessObservationLease : IAsyncDisposable
{
    /// <summary>Streams cloned authoritative readiness snapshots until cancellation or lease disposal.</summary>
    IAsyncEnumerable<ManagedCodexCredentialSnapshot> ReadAllAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Accepted-only command boundary for the per-user managed Codex credential actor.
/// </summary>
public interface IManagedCodexCredentialCommandPort
{
    /// <summary>Admits an initial provisioned descriptor.</summary>
    Task<DispatchAdmission> CommitProvisionedAsync(
        ManagedCodexCredentialDescriptor credential,
        IReadOnlyList<ManagedCodexCredentialCleanup> obsoleteCredentialCleanups,
        CancellationToken ct = default);

    /// <summary>
    /// Admits a rotation guarded by the exact Actor-owned previous-credential
    /// cleanup that must be committed atomically with the new descriptor.
    /// </summary>
    Task<DispatchAdmission> CommitRotatedAsync(
        string expectedPreviousApiKeyId,
        ManagedCodexCredentialDescriptor credential,
        ManagedCodexCredentialCleanup previousCredentialCleanup,
        IReadOnlyList<ManagedCodexCredentialCleanup> obsoleteCredentialCleanups,
        CancellationToken ct = default);

    /// <summary>Admits policy reconciliation while preserving the current API key and Vault reference.</summary>
    Task<DispatchAdmission> CommitPolicyReconciledAsync(
        string expectedApiKeyId,
        ManagedCodexCredentialDescriptor credential,
        IReadOnlyList<ManagedCodexCredentialCleanup> obsoleteCredentialCleanups,
        CancellationToken ct = default);

    /// <summary>Admits an idempotent readiness confirmation for the exact expected active credential.</summary>
    Task<DispatchAdmission> ConfirmReadinessAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor expectedCredential,
        ManagedCodexCredentialReadinessEvidence readinessEvidence,
        CancellationToken ct = default);

    /// <summary>Admits credential revocation and its independently retryable cleanup tracks.</summary>
    Task<DispatchAdmission> CommitRevokedAsync(
        ExternalSubjectRef owner,
        string expectedApiKeyId,
        ManagedCodexCredentialCleanup cleanup,
        DateTimeOffset revokedAt,
        CancellationToken ct = default);

    /// <summary>Admits cleanup work created before a credential descriptor could be adopted.</summary>
    Task<DispatchAdmission> QueueCleanupAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialCleanup cleanup,
        CancellationToken ct = default);

    /// <summary>Records one successfully completed external cleanup track.</summary>
    Task<DispatchAdmission> CompleteCleanupTrackAsync(
        ExternalSubjectRef owner,
        string apiKeyId,
        string secretRef,
        ManagedCodexCredentialCleanupTrack track,
        DateTimeOffset completedAt,
        CancellationToken ct = default);
}
