using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.Identity;

[GAgent("channel.identity.managed-codex-credential")]
public sealed class ManagedCodexCredentialGAgent : GAgentBase<ManagedCodexCredentialState>
{
    protected override ManagedCodexCredentialState TransitionState(
        ManagedCodexCredentialState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ManagedCodexCredentialProvisionedEvent>(ApplyProvisioned)
            .On<ManagedCodexCredentialRotatedEvent>(ApplyRotated)
            .On<ManagedCodexCredentialPolicyReconciledEvent>(ApplyPolicyReconciled)
            .On<ManagedCodexCredentialReadinessConfirmedEvent>(static (state, _) => state.Clone())
            .On<ManagedCodexCredentialRevokedEvent>(ApplyRevoked)
            .On<ManagedCodexCredentialCleanupQueuedEvent>(ApplyCleanupQueued)
            .On<ManagedCodexCredentialCleanupTrackCompletedEvent>(ApplyCleanupTrackCompleted)
            .OrCurrent();

    [EventHandler]
    public async Task HandleProvisioned(CommitManagedCodexCredentialProvisionedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!TryValidateCredential(command.Credential, out var credential))
            return;

        if (State.Credential is { Status: ManagedCodexCredentialStatus.Active } current)
        {
            if (current.Equals(credential))
            {
                await PersistReadinessConfirmedAsync(
                    current,
                    ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
                return;
            }

            if (string.Equals(current.ApiKeyId, credential.ApiKeyId, StringComparison.Ordinal))
                return;

            await QueueIncomingCredentialCleanupAsync(credential);
            return;
        }

        await PersistDomainEventAsync(new ManagedCodexCredentialProvisionedEvent
        {
            Credential = credential,
        });
    }

    [EventHandler]
    public async Task HandleRotated(CommitManagedCodexCredentialRotatedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!TryValidateCredential(command.Credential, out var credential))
            return;

        var current = State.Credential;
        if (current is not null && current.Equals(credential))
        {
            await PersistReadinessConfirmedAsync(
                current,
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
            return;
        }

        if (current is null ||
            current.Status != ManagedCodexCredentialStatus.Active ||
            !string.Equals(current.ApiKeyId, command.ExpectedPreviousApiKeyId?.Trim(), StringComparison.Ordinal))
        {
            await QueueIncomingCredentialCleanupAsync(credential);
            return;
        }

        var currentReference = current.SecretReference;
        var rotatedReference = credential.SecretReference;
        if (currentReference is null ||
            rotatedReference is null ||
            string.Equals(currentReference.Ref, rotatedReference.Ref, StringComparison.Ordinal))
        {
            Logger.LogWarning("Managed Codex rotation rejected because it did not use a new Vault reference.");
            await QueueIncomingCredentialCleanupAsync(credential);
            return;
        }

        var previousCleanup = NormalizeCleanup(
            command.PreviousCredentialCleanup,
            null,
            null);
        if (!IsExactPreviousCredentialCleanup(
                current,
                credential,
                previousCleanup))
        {
            Logger.LogWarning(
                "Managed Codex rotation rejected because previous-credential cleanup did not match authoritative state.");
            await QueueIncomingCredentialCleanupAsync(credential);
            return;
        }

        await PersistDomainEventAsync(new ManagedCodexCredentialRotatedEvent
        {
            PreviousApiKeyId = current.ApiKeyId,
            Credential = credential,
            PreviousCredentialCleanup = previousCleanup,
        });
    }

    [EventHandler]
    public async Task HandlePolicyReconciled(
        CommitManagedCodexCredentialPolicyReconciledCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!TryValidateCredential(command.Credential, out var credential))
            return;

        var current = State.Credential;
        if (current is null ||
            current.Status != ManagedCodexCredentialStatus.Active ||
            !string.Equals(current.ApiKeyId, command.ExpectedApiKeyId?.Trim(), StringComparison.Ordinal) ||
            !string.Equals(current.ApiKeyId, credential.ApiKeyId, StringComparison.Ordinal) ||
            !Equals(current.SecretReference, credential.SecretReference))
        {
            await QueueIncomingCredentialCleanupAsync(credential);
            return;
        }

        if (current.Equals(credential))
        {
            await PersistReadinessConfirmedAsync(
                current,
                ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed);
            return;
        }

        await PersistDomainEventAsync(new ManagedCodexCredentialPolicyReconciledEvent
        {
            ApiKeyId = current.ApiKeyId,
            Credential = credential,
        });
    }

    [EventHandler]
    public async Task HandleReadinessConfirmation(
        ConfirmManagedCodexCredentialReadinessCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!MatchesActor(command.Owner) ||
            string.IsNullOrWhiteSpace(command.ExpectedApiKeyId) ||
            !TryValidateCredential(command.ExpectedCredential, out var expectedCredential) ||
            command.ReadinessEvidence is not
                (ManagedCodexCredentialReadinessEvidence.CurrentStateConfirmed or
                 ManagedCodexCredentialReadinessEvidence.RemoteValidated))
        {
            return;
        }

        var current = State.Credential;
        if (current is null ||
            current.Status != ManagedCodexCredentialStatus.Active ||
            !string.Equals(
                current.ApiKeyId,
                command.ExpectedApiKeyId.Trim(),
                StringComparison.Ordinal) ||
            !current.Equals(expectedCredential))
        {
            return;
        }

        await PersistReadinessConfirmedAsync(
            current,
            command.ReadinessEvidence);
    }

    [EventHandler]
    public async Task HandleRevoked(CommitManagedCodexCredentialRevokedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!MatchesActor(command.Owner) || command.RevokedAt is null)
            return;

        var current = State.Credential;
        if (current is null ||
            !string.Equals(current.ApiKeyId, command.ExpectedApiKeyId?.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        if (current.Status == ManagedCodexCredentialStatus.Revoked)
            return;

        await PersistDomainEventAsync(new ManagedCodexCredentialRevokedEvent
        {
            Owner = command.Owner.Clone(),
            RevokedAt = command.RevokedAt.Clone(),
            Cleanup = NormalizeCleanup(command.Cleanup, current.ApiKeyId, current.SecretReference?.Ref),
        });
    }

    [EventHandler]
    public async Task HandleCleanupQueued(QueueManagedCodexCredentialCleanupCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!MatchesActor(command.Owner))
            return;

        var cleanup = NormalizeCleanup(command.Cleanup, null, null);
        if (cleanup is null || (!cleanup.NyxIdPending && !cleanup.VaultPending))
            return;
        if (TargetsActiveCredential(cleanup))
        {
            Logger.LogWarning(
                "Managed Codex generic cleanup rejected because it targets the active credential.");
            return;
        }

        await PersistDomainEventAsync(new ManagedCodexCredentialCleanupQueuedEvent
        {
            Owner = command.Owner.Clone(),
            Cleanup = cleanup,
        });
    }

    [EventHandler]
    public async Task HandleCleanupTrackCompleted(CompleteManagedCodexCredentialCleanupTrackCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!MatchesActor(command.Owner) ||
            string.IsNullOrWhiteSpace(command.ApiKeyId) ||
            command.Track == ManagedCodexCredentialCleanupTrack.Unspecified ||
            command.CompletedAt is null ||
            State.PendingRevocations.All(item =>
                !string.Equals(item.ApiKeyId, command.ApiKeyId.Trim(), StringComparison.Ordinal)))
        {
            return;
        }

        await PersistDomainEventAsync(new ManagedCodexCredentialCleanupTrackCompletedEvent
        {
            Owner = command.Owner.Clone(),
            ApiKeyId = command.ApiKeyId.Trim(),
            Track = command.Track,
            CompletedAt = command.CompletedAt.Clone(),
        });
    }

    private Task PersistReadinessConfirmedAsync(
        ManagedCodexCredentialDescriptor credential,
        ManagedCodexCredentialReadinessEvidence readinessEvidence) =>
        PersistDomainEventAsync(new ManagedCodexCredentialReadinessConfirmedEvent
        {
            ApiKeyId = credential.ApiKeyId,
            VerifiedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ReadinessEvidence = readinessEvidence,
        });

    private async Task QueueIncomingCredentialCleanupAsync(ManagedCodexCredentialDescriptor credential)
    {
        if (State.Credential is { Status: ManagedCodexCredentialStatus.Active } current &&
            string.Equals(current.ApiKeyId, credential.ApiKeyId, StringComparison.Ordinal))
        {
            Logger.LogWarning(
                "Managed Codex cleanup rejected because the incoming descriptor identifies the active API key.");
            return;
        }

        var sameVaultRef = string.Equals(
            State.Credential?.SecretReference?.Ref,
            credential.SecretReference?.Ref,
            StringComparison.Ordinal);
        await PersistDomainEventAsync(new ManagedCodexCredentialCleanupQueuedEvent
        {
            Owner = credential.Owner.Clone(),
            Cleanup = new ManagedCodexCredentialCleanup
            {
                ApiKeyId = credential.ApiKeyId,
                SecretRef = credential.SecretReference?.Ref ?? string.Empty,
                NyxIdPending = true,
                VaultPending = !sameVaultRef,
                RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        });
    }

    private bool TryValidateCredential(
        ManagedCodexCredentialDescriptor? candidate,
        out ManagedCodexCredentialDescriptor credential)
    {
        credential = null!;
        var expectedOwnerScopeKey = candidate?.Owner is null
            ? string.Empty
            : TryResolveOwnerScopeKey(candidate.Owner);
        if (candidate?.Owner is null || !MatchesActor(candidate.Owner) ||
            string.IsNullOrWhiteSpace(candidate.ApiKeyId) ||
            candidate.SecretReference is null ||
            string.IsNullOrWhiteSpace(candidate.SecretReference.Ref) ||
            !string.Equals(
                candidate.SecretReference.Purpose,
                CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.SecretReference.OwnerScopeKey,
                expectedOwnerScopeKey,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(candidate.SecretReference.Fingerprint) ||
            candidate.SecretReference.Version <= 0 ||
            string.IsNullOrWhiteSpace(candidate.ChronoSandboxUserServiceId) ||
            string.IsNullOrWhiteSpace(candidate.ChronoLlmUserServiceId) ||
            string.Equals(
                candidate.ChronoSandboxUserServiceId.Trim(),
                candidate.ChronoLlmUserServiceId.Trim(),
                StringComparison.Ordinal) ||
            !string.Equals(candidate.ChronoSandboxServiceSlug, "chrono-sandbox", StringComparison.Ordinal) ||
            candidate.ExpiresAt is null ||
            candidate.SecretReference.ExpiresAtUnixMs !=
                candidate.ExpiresAt.ToDateTimeOffset().ToUnixTimeMilliseconds() ||
            candidate.Status != ManagedCodexCredentialStatus.Active)
        {
            Logger.LogWarning("Managed Codex credential command rejected because its descriptor is invalid.");
            return false;
        }

        credential = candidate.Clone();
        credential.ApiKeyId = credential.ApiKeyId.Trim();
        credential.ChronoSandboxUserServiceId = credential.ChronoSandboxUserServiceId.Trim();
        credential.ChronoLlmUserServiceId = credential.ChronoLlmUserServiceId.Trim();
        return true;
    }

    private static string TryResolveOwnerScopeKey(ExternalSubjectRef owner)
    {
        try
        {
            return ManagedCodexCredentialActorIdentity.From(owner);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private bool MatchesActor(ExternalSubjectRef? owner)
    {
        if (owner is null)
            return false;

        string expected;
        try
        {
            expected = ManagedCodexCredentialActorIdentity.From(owner);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return string.IsNullOrEmpty(Id) || string.Equals(Id, expected, StringComparison.Ordinal);
    }

    private bool TargetsActiveCredential(ManagedCodexCredentialCleanup cleanup)
    {
        var current = State.Credential;
        return current is { Status: ManagedCodexCredentialStatus.Active } &&
               (cleanup.NyxIdPending &&
                string.Equals(
                    cleanup.ApiKeyId,
                    current.ApiKeyId,
                    StringComparison.Ordinal) ||
                cleanup.VaultPending &&
                string.Equals(
                    cleanup.SecretRef,
                    current.SecretReference?.Ref,
                    StringComparison.Ordinal));
    }

    private static bool IsExactPreviousCredentialCleanup(
        ManagedCodexCredentialDescriptor current,
        ManagedCodexCredentialDescriptor rotated,
        ManagedCodexCredentialCleanup? cleanup)
    {
        if (cleanup is null ||
            current.SecretReference is null ||
            rotated.SecretReference is null ||
            !string.Equals(
                cleanup.ApiKeyId,
                current.ApiKeyId,
                StringComparison.Ordinal) ||
            !string.Equals(
                cleanup.SecretRef,
                current.SecretReference.Ref,
                StringComparison.Ordinal))
        {
            return false;
        }

        var nyxIdPending = !string.Equals(
            current.ApiKeyId,
            rotated.ApiKeyId,
            StringComparison.Ordinal);
        var vaultPending = !string.Equals(
            current.SecretReference.Ref,
            rotated.SecretReference.Ref,
            StringComparison.Ordinal);
        return cleanup.NyxIdPending == nyxIdPending &&
               cleanup.VaultPending == vaultPending &&
               (nyxIdPending || vaultPending);
    }

    private static ManagedCodexCredentialCleanup? NormalizeCleanup(
        ManagedCodexCredentialCleanup? cleanup,
        string? fallbackApiKeyId,
        string? fallbackSecretRef)
    {
        if (cleanup is null)
            return null;

        var normalized = cleanup.Clone();
        normalized.ApiKeyId = string.IsNullOrWhiteSpace(normalized.ApiKeyId)
            ? fallbackApiKeyId?.Trim() ?? string.Empty
            : normalized.ApiKeyId.Trim();
        normalized.SecretRef = string.IsNullOrWhiteSpace(normalized.SecretRef)
            ? fallbackSecretRef?.Trim() ?? string.Empty
            : normalized.SecretRef.Trim();
        normalized.RequestedAt ??= Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        return string.IsNullOrWhiteSpace(normalized.ApiKeyId) ? null : normalized;
    }

    private static ManagedCodexCredentialState ApplyProvisioned(
        ManagedCodexCredentialState current,
        ManagedCodexCredentialProvisionedEvent evt)
    {
        var next = current.Clone();
        next.Credential = evt.Credential?.Clone();
        next.RevokedAt = null;
        return next;
    }

    private static ManagedCodexCredentialState ApplyRotated(
        ManagedCodexCredentialState current,
        ManagedCodexCredentialRotatedEvent evt)
    {
        var next = current.Clone();
        next.Credential = evt.Credential?.Clone();
        next.RevokedAt = null;
        AddOrMergeCleanup(next, evt.PreviousCredentialCleanup);
        return next;
    }

    private static ManagedCodexCredentialState ApplyPolicyReconciled(
        ManagedCodexCredentialState current,
        ManagedCodexCredentialPolicyReconciledEvent evt)
    {
        var next = current.Clone();
        next.Credential = evt.Credential?.Clone();
        next.RevokedAt = null;
        return next;
    }

    private static ManagedCodexCredentialState ApplyRevoked(
        ManagedCodexCredentialState current,
        ManagedCodexCredentialRevokedEvent evt)
    {
        var next = current.Clone();
        if (next.Credential is not null)
            next.Credential.Status = ManagedCodexCredentialStatus.Revoked;
        next.RevokedAt = evt.RevokedAt?.Clone();
        AddOrMergeCleanup(next, evt.Cleanup);
        return next;
    }

    private static ManagedCodexCredentialState ApplyCleanupQueued(
        ManagedCodexCredentialState current,
        ManagedCodexCredentialCleanupQueuedEvent evt)
    {
        var next = current.Clone();
        AddOrMergeCleanup(next, evt.Cleanup);
        return next;
    }

    private static ManagedCodexCredentialState ApplyCleanupTrackCompleted(
        ManagedCodexCredentialState current,
        ManagedCodexCredentialCleanupTrackCompletedEvent evt)
    {
        var next = current.Clone();
        var cleanup = next.PendingRevocations.FirstOrDefault(item =>
            string.Equals(item.ApiKeyId, evt.ApiKeyId, StringComparison.Ordinal));
        if (cleanup is null)
            return next;

        if (evt.Track == ManagedCodexCredentialCleanupTrack.NyxId)
            cleanup.NyxIdPending = false;
        else if (evt.Track == ManagedCodexCredentialCleanupTrack.Vault)
            cleanup.VaultPending = false;

        if (!cleanup.NyxIdPending && !cleanup.VaultPending)
            next.PendingRevocations.Remove(cleanup);
        return next;
    }

    private static void AddOrMergeCleanup(
        ManagedCodexCredentialState state,
        ManagedCodexCredentialCleanup? cleanup)
    {
        if (cleanup is null || string.IsNullOrWhiteSpace(cleanup.ApiKeyId) ||
            (!cleanup.NyxIdPending && !cleanup.VaultPending))
        {
            return;
        }

        var existing = state.PendingRevocations.FirstOrDefault(item =>
            string.Equals(item.ApiKeyId, cleanup.ApiKeyId, StringComparison.Ordinal));
        if (existing is null)
        {
            state.PendingRevocations.Add(cleanup.Clone());
            return;
        }

        existing.NyxIdPending |= cleanup.NyxIdPending;
        existing.VaultPending |= cleanup.VaultPending;
        if (string.IsNullOrWhiteSpace(existing.SecretRef))
            existing.SecretRef = cleanup.SecretRef;
        existing.RequestedAt ??= cleanup.RequestedAt?.Clone();
    }
}
