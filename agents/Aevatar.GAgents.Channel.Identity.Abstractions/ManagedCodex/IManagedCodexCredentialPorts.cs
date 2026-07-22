using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Current non-secret managed Codex credential facts materialized from the
/// authoritative per-user actor.
/// </summary>
public sealed record ManagedCodexCredentialSnapshot(
    ManagedCodexCredentialDescriptor Credential,
    IReadOnlyList<ManagedCodexCredentialCleanup> PendingRevocations,
    long StateVersion);

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
/// Accepted-only command boundary for the per-user managed Codex credential actor.
/// </summary>
public interface IManagedCodexCredentialCommandPort
{
    /// <summary>Admits an initial provisioned descriptor.</summary>
    Task<DispatchAdmission> CommitProvisionedAsync(
        ManagedCodexCredentialDescriptor credential,
        CancellationToken ct = default);

    /// <summary>Admits a rotation guarded by the expected current NyxID API key ID.</summary>
    Task<DispatchAdmission> CommitRotatedAsync(
        string expectedPreviousApiKeyId,
        ManagedCodexCredentialDescriptor credential,
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
        ManagedCodexCredentialCleanupTrack track,
        DateTimeOffset completedAt,
        CancellationToken ct = default);
}
