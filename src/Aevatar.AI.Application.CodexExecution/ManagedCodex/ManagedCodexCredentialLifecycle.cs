using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.AI.Application.CodexExecution;

public interface IManagedCodexCredentialLifecycle
{
    Task<ManagedCodexCredentialMutationResult> ProvisionAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default);

    Task<ManagedCodexCredentialMutationResult> RotateAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default);

    Task<ManagedCodexCredentialMutationResult> RevokeAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default);
}

public sealed record ManagedCodexCredentialMutationResult(
    string Status,
    string ActorId,
    string ApiKeyId,
    long ExpiresAtUnixMs,
    string CommandId);

public sealed class ManagedCodexCredentialLifecycleException : Exception
{
    public ManagedCodexCredentialLifecycleException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class ManagedCodexCredentialLifecycle(
    IOptions<ManagedCodexOptions> options,
    IManagedCodexNyxIdCredentialPort nyxIdPort,
    ISecretVault secretVault,
    IManagedCodexCredentialQueryPort queryPort,
    IManagedCodexCredentialCommandPort commandPort,
    IManagedCodexCredentialMutationLease mutationLease,
    TimeProvider timeProvider,
    ILogger<ManagedCodexCredentialLifecycle> logger) : IManagedCodexCredentialLifecycle
{
    internal const string CredentialName = "aevatar-managed-codex";

    private readonly ManagedCodexOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IManagedCodexNyxIdCredentialPort _nyxIdPort =
        nyxIdPort ?? throw new ArgumentNullException(nameof(nyxIdPort));
    private readonly ISecretVault _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    private readonly IManagedCodexCredentialQueryPort _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    private readonly IManagedCodexCredentialCommandPort _commandPort = commandPort ?? throw new ArgumentNullException(nameof(commandPort));
    private readonly IManagedCodexCredentialMutationLease _mutationLease =
        mutationLease ?? throw new ArgumentNullException(nameof(mutationLease));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<ManagedCodexCredentialLifecycle> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ManagedCodexNyxIdCatalogResolver _catalogResolver = new();

    public async Task<ManagedCodexCredentialMutationResult> ProvisionAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default)
    {
        EnsureEnabled();
        var owner = await ResolveOwnerAsync(bearerToken, authenticatedUserId, requireEligibility: true, ct);
        await using var lease = await AcquireMutationLeaseAsync(owner, ct).ConfigureAwait(false);
        var existing = await _queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
        using var completion = BeginMutationCompletion(ct);
        var mutationCt = completion.Token;
        await RetryPendingCleanupAsync(bearerToken, owner, existing?.PendingRevocations, mutationCt)
            .ConfigureAwait(false);
        if (existing?.Credential.Status == ManagedCodexCredentialStatus.Active)
            throw Failure("managed_credential_already_provisioned", "Managed Codex is already provisioned for this user; rotate it instead.");

        var eligibility = await _catalogResolver.ResolveAsync(_nyxIdPort, bearerToken, mutationCt)
            .ConfigureAwait(false);
        var activeKeys = await GetActiveManagedKeysAsync(bearerToken, mutationCt).ConfigureAwait(false);
        EnsureAtMostOneActiveKey(activeKeys);
        if (activeKeys.Count == 1)
        {
            var recovered = await TryReconcileProvisionAsync(
                bearerToken,
                owner,
                activeKeys[0],
                eligibility,
                mutationCt).ConfigureAwait(false);
            if (recovered is not null)
                return recovered;
        }

        var requestedExpiresAt = _timeProvider.GetUtcNow().AddDays(_options.CredentialLifetimeDays);
        var issued = await _nyxIdPort.CreateApiKeyAsync(
            bearerToken,
            IssueRequest(eligibility, requestedExpiresAt),
            mutationCt).ConfigureAwait(false);
        ManagedCodexNyxIdApiKey persistedKey;
        try
        {
            ValidateIssuedKey(issued.Key, eligibility);
            persistedKey = await RequirePersistedIssuedKeyAsync(
                bearerToken,
                issued.Key.Id,
                eligibility,
                requestedExpiresAt,
                mutationCt).ConfigureAwait(false);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            if (!string.IsNullOrWhiteSpace(issued.Key.Id))
            {
                await CompensateRejectedIssuanceAsync(
                    bearerToken,
                    owner,
                    issued.Key.Id,
                    mutationCt).ConfigureAwait(false);
            }
            throw;
        }

        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var expiresAt = persistedKey.ExpiresAt!.Value;
        var requestedRef = SecretRefFor(actorId, persistedKey.Id);
        StoreSecretResult stored;
        try
        {
            stored = await issued.Secret.UseAsync(secret => _secretVault.PutAsync(
                new StoreSecretRequest(
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    actorId,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    secret,
                    "managed-codex-provision",
                    expiresAt,
                    requestedRef),
                mutationCt)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CompensateUnadoptedAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                mutationCt).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await CompensateUnadoptedAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                mutationCt).ConfigureAwait(false);
            throw Failure(
                "managed_credential_vault_store_failed",
                "The managed Codex credential could not be stored securely.");
        }

        SecretReference reference;
        try
        {
            reference = ValidateStoredReference(stored.Reference, requestedRef, actorId, expiresAt);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            await CompensateUnadoptedAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                mutationCt).ConfigureAwait(false);
            throw;
        }
        var descriptor = BuildDescriptor(
            owner,
            persistedKey.Id,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            expiresAt);
        DispatchAdmission admission;
        try
        {
            admission = await _commandPort.CommitProvisionedAsync(descriptor, mutationCt).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw Failure(
                "managed_credential_persistence_pending",
                "Managed Codex credential persistence is pending reconciliation.");
        }
        if (!admission.Accepted)
        {
            throw Failure(
                "managed_credential_persistence_pending",
                "Managed Codex credential persistence is pending reconciliation.");
        }

        return Result("provisioning_accepted", actorId, persistedKey.Id, expiresAt, admission);
    }

    public async Task<ManagedCodexCredentialMutationResult> RotateAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default)
    {
        EnsureEnabled();
        var owner = await ResolveOwnerAsync(bearerToken, authenticatedUserId, requireEligibility: true, ct);
        await using var lease = await AcquireMutationLeaseAsync(owner, ct).ConfigureAwait(false);
        var current = await RequireActiveAsync(owner, ct).ConfigureAwait(false);
        using var completion = BeginMutationCompletion(ct);
        var mutationCt = completion.Token;
        await RetryPendingCleanupAsync(bearerToken, owner, current.PendingRevocations, mutationCt)
            .ConfigureAwait(false);
        var eligibility = await _catalogResolver.ResolveAsync(_nyxIdPort, bearerToken, mutationCt).ConfigureAwait(false);
        if (!string.Equals(
                current.Credential.ChronoSandboxUserServiceId,
                eligibility.ChronoSandboxUserServiceId,
                StringComparison.Ordinal))
        {
            throw Failure(
                "chrono_sandbox_service_changed",
                "The user's chrono-sandbox service changed; revoke and provision a new scoped credential.");
        }

        ValidateActiveDescriptor(current.Credential, owner, _timeProvider.GetUtcNow());
        var activeKeys = await GetActiveManagedKeysAsync(bearerToken, mutationCt).ConfigureAwait(false);
        EnsureAtMostOneActiveKey(activeKeys);
        if (activeKeys.Count == 1 &&
            !string.Equals(
                activeKeys[0].Id,
                current.Credential.ApiKeyId,
                StringComparison.Ordinal))
        {
            var recovered = await TryReconcileRotationAsync(
                bearerToken,
                owner,
                current.Credential,
                activeKeys[0],
                eligibility,
                mutationCt).ConfigureAwait(false);
            if (recovered is not null)
                return recovered;
            activeKeys = [];
        }

        var expiresAt = current.Credential.ExpiresAt.ToDateTimeOffset();
        ManagedCodexNyxIdIssuedApiKey issued;
        if (activeKeys.Count == 1)
        {
            ValidatePersistedKey(
                activeKeys[0],
                eligibility,
                expiresAt);
            issued = await _nyxIdPort.RotateApiKeyAsync(
                bearerToken,
                current.Credential.ApiKeyId,
                mutationCt).ConfigureAwait(false);
        }
        else
        {
            issued = await _nyxIdPort.CreateApiKeyAsync(
                bearerToken,
                IssueRequest(eligibility, expiresAt),
                mutationCt).ConfigureAwait(false);
        }

        ManagedCodexNyxIdApiKey persistedKey;
        try
        {
            ValidateIssuedKey(issued.Key, eligibility);
            persistedKey = await RequirePersistedIssuedKeyAsync(
                bearerToken,
                issued.Key.Id,
                eligibility,
                expiresAt,
                mutationCt).ConfigureAwait(false);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            await CompensateRejectedIssuanceAsync(
                bearerToken,
                owner,
                issued.Key.Id,
                mutationCt).ConfigureAwait(false);
            throw;
        }

        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var requestedRef = SecretRefFor(actorId, persistedKey.Id);
        StoreSecretResult stored;
        try
        {
            stored = await issued.Secret.UseAsync(secret => _secretVault.PutAsync(
                new StoreSecretRequest(
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    actorId,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    secret,
                    "managed-codex-rotate",
                    expiresAt,
                    requestedRef),
                mutationCt)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CompensateUnadoptedAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                mutationCt).ConfigureAwait(false);
            throw Failure(
                "managed_credential_vault_store_failed",
                "The managed Codex credential could not be rotated securely.");
        }

        SecretReference newReference;
        try
        {
            newReference = ValidateStoredReference(stored.Reference, requestedRef, actorId, expiresAt);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            await CompensateUnadoptedAsync(
                bearerToken,
                owner,
                persistedKey.Id,
                requestedRef,
                actorId,
                mutationCt).ConfigureAwait(false);
            throw;
        }

        var descriptor = BuildDescriptor(
            owner,
            persistedKey.Id,
            newReference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            expiresAt);
        DispatchAdmission admission;
        try
        {
            admission = await _commandPort.CommitRotatedAsync(
                current.Credential.ApiKeyId,
                descriptor,
                mutationCt).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw Failure(
                "managed_credential_persistence_pending",
                "Managed Codex credential persistence is pending reconciliation.");
        }
        if (!admission.Accepted)
        {
            throw Failure(
                "managed_credential_persistence_pending",
                "Managed Codex credential persistence is pending reconciliation.");
        }

        await RetirePreviousVaultReferenceAsync(owner, current.Credential, mutationCt).ConfigureAwait(false);
        return Result("rotation_accepted", actorId, persistedKey.Id, expiresAt, admission);
    }

    public async Task<ManagedCodexCredentialMutationResult> RevokeAsync(
        string bearerToken,
        string authenticatedUserId,
        CancellationToken ct = default)
    {
        var owner = await ResolveOwnerAsync(bearerToken, authenticatedUserId, requireEligibility: false, ct);
        await using var lease = await AcquireMutationLeaseAsync(owner, ct).ConfigureAwait(false);
        var current = await RequireActiveAsync(owner, ct).ConfigureAwait(false);
        using var completion = BeginMutationCompletion(ct);
        var mutationCt = completion.Token;
        await RetryPendingCleanupAsync(bearerToken, owner, current.PendingRevocations, mutationCt)
            .ConfigureAwait(false);
        var credential = current.Credential;
        ValidateActiveDescriptor(credential, owner, _timeProvider.GetUtcNow(), requireFutureExpiry: false);

        var vaultPending = !await TryRevokeVaultAsync(
            credential.SecretReference.Ref,
            credential.SecretReference.OwnerScopeKey,
            "managed-codex-revoke",
            mutationCt).ConfigureAwait(false);
        var nyxIdPending = !await TryDeleteNyxIdKeyAsync(bearerToken, credential.ApiKeyId, mutationCt)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var cleanup = new ManagedCodexCredentialCleanup
        {
            ApiKeyId = credential.ApiKeyId,
            SecretRef = credential.SecretReference.Ref,
            NyxIdPending = nyxIdPending,
            VaultPending = vaultPending,
            RequestedAt = Timestamp.FromDateTimeOffset(now),
        };
        DispatchAdmission admission;
        try
        {
            admission = await _commandPort.CommitRevokedAsync(
                owner,
                credential.ApiKeyId,
                cleanup,
                now,
                mutationCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Failure(
                "managed_credential_command_failed",
                "The managed Codex revocation could not be submitted for persistence.");
        }
        if (!admission.Accepted)
            throw Failure("managed_credential_command_rejected", "The managed Codex revocation was not accepted for persistence.");

        return Result(
            "revocation_accepted",
            ManagedCodexCredentialActorIdentity.From(owner),
            credential.ApiKeyId,
            credential.ExpiresAt?.ToDateTimeOffset() ?? now,
            admission);
    }

    private async Task<ExternalSubjectRef> ResolveOwnerAsync(
        string bearerToken,
        string authenticatedUserId,
        bool requireEligibility,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedUserId);
        var currentUserId = await _nyxIdPort.GetCurrentUserIdAsync(bearerToken.Trim(), ct)
            .ConfigureAwait(false);
        if (!string.Equals(currentUserId, authenticatedUserId.Trim(), StringComparison.Ordinal))
            throw Failure("nyxid_identity_mismatch", "The authenticated identity does not own the supplied NyxID bearer.");
        if (requireEligibility && !_options.IsEligible(currentUserId))
            throw Failure("managed_feature_not_enabled", "Managed Codex is not enabled for this user.");

        return new ExternalSubjectRef
        {
            Platform = OwnerScope.NyxIdPlatform,
            Tenant = string.Empty,
            ExternalUserId = currentUserId,
        };
    }

    private async Task<ManagedCodexCredentialSnapshot> RequireActiveAsync(
        ExternalSubjectRef owner,
        CancellationToken ct)
    {
        var snapshot = await _queryPort.ResolveAsync(owner, ct).ConfigureAwait(false);
        if (snapshot?.Credential is null || snapshot.Credential.Status != ManagedCodexCredentialStatus.Active)
            throw Failure("managed_credential_not_provisioned", "Managed Codex is not provisioned for this user.");
        return snapshot;
    }

    private async ValueTask<IManagedCodexCredentialMutationLeaseHandle> AcquireMutationLeaseAsync(
        ExternalSubjectRef owner,
        CancellationToken ct)
    {
        var ownerKey = ManagedCodexCredentialActorIdentity.From(owner);
        return await _mutationLease.TryAcquireAsync(ownerKey, ct).ConfigureAwait(false)
               ?? throw Failure(
                   "managed_credential_mutation_in_progress",
                   "Another managed Codex credential mutation is already in progress for this user.");
    }

    private CancellationTokenSource BeginMutationCompletion(CancellationToken requestToken)
    {
        requestToken.ThrowIfCancellationRequested();
        return new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.MutationCompletionSeconds),
            _timeProvider);
    }

    private static ManagedCodexNyxIdApiKeyIssueRequest IssueRequest(
        ManagedCodexNyxIdEligibility eligibility,
        DateTimeOffset expiresAt) =>
        new(
            CredentialName,
            "Aevatar managed codex_exec invocation credential",
            "proxy",
            "codex",
            false,
            [
                eligibility.ChronoSandboxUserServiceId,
                eligibility.ChronoLlmUserServiceId,
            ],
            false,
            [],
            expiresAt);

    private static ManagedCodexNyxIdApiKeyPolicyUpdateRequest PolicyUpdateRequest(
        ManagedCodexNyxIdEligibility eligibility) =>
        new(
            "proxy",
            "codex",
            false,
            [
                eligibility.ChronoSandboxUserServiceId,
                eligibility.ChronoLlmUserServiceId,
            ],
            false,
            []);

    private async Task<IReadOnlyList<ManagedCodexNyxIdApiKey>> GetActiveManagedKeysAsync(
        string bearerToken,
        CancellationToken ct)
    {
        var keys = await _nyxIdPort.ListApiKeysAsync(bearerToken, ct).ConfigureAwait(false);
        return keys
            .Where(static key =>
                key.IsActive &&
                string.Equals(key.Name, CredentialName, StringComparison.Ordinal))
            .ToArray();
    }

    private static void EnsureAtMostOneActiveKey(
        IReadOnlyList<ManagedCodexNyxIdApiKey> activeKeys)
    {
        if (activeKeys.Count <= 1)
            return;

        throw Failure(
            "managed_credential_untracked_key_exists",
            "Multiple active managed Codex keys exist in NyxID and must be reconciled.");
    }

    private async Task<ManagedCodexCredentialMutationResult?> TryReconcileProvisionAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ManagedCodexNyxIdApiKey activeKey,
        ManagedCodexNyxIdEligibility eligibility,
        CancellationToken ct)
    {
        try
        {
            ValidatePersistedKey(activeKey, eligibility, expectedExpiresAt: null);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            _ = await CompensateRejectedIssuanceAsync(
                bearerToken,
                owner,
                activeKey.Id,
                ct).ConfigureAwait(false);
            throw;
        }

        var reference = await TryResolveIssuedReferenceAsync(owner, activeKey, ct)
            .ConfigureAwait(false);
        if (reference is null)
        {
            if (!await CompensateRejectedIssuanceAsync(
                    bearerToken,
                    owner,
                    activeKey.Id,
                    ct).ConfigureAwait(false))
            {
                throw Failure(
                    "managed_credential_cleanup_pending",
                    "The untracked managed Codex key could not be revoked; retry later.");
            }
            return null;
        }

        var descriptor = BuildDescriptor(
            owner,
            activeKey.Id,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            activeKey.ExpiresAt!.Value);
        var admission = await CommitProvisionedForReconciliationAsync(descriptor, ct)
            .ConfigureAwait(false);
        return Result(
            "provisioning_reconciliation_accepted",
            ManagedCodexCredentialActorIdentity.From(owner),
            activeKey.Id,
            activeKey.ExpiresAt.Value,
            admission);
    }

    private async Task<ManagedCodexCredentialMutationResult?> TryReconcileRotationAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor current,
        ManagedCodexNyxIdApiKey activeKey,
        ManagedCodexNyxIdEligibility eligibility,
        CancellationToken ct)
    {
        try
        {
            ValidatePersistedKey(activeKey, eligibility, expectedExpiresAt: null);
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            _ = await CompensateRejectedIssuanceAsync(
                bearerToken,
                owner,
                activeKey.Id,
                ct).ConfigureAwait(false);
            throw;
        }

        var reference = await TryResolveIssuedReferenceAsync(owner, activeKey, ct)
            .ConfigureAwait(false);
        if (reference is null)
        {
            if (!await CompensateRejectedIssuanceAsync(
                    bearerToken,
                    owner,
                    activeKey.Id,
                    ct).ConfigureAwait(false))
            {
                throw Failure(
                    "managed_credential_cleanup_pending",
                    "The unrecoverable managed Codex key could not be revoked; retry later.");
            }
            return null;
        }

        var descriptor = BuildDescriptor(
            owner,
            activeKey.Id,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            activeKey.ExpiresAt!.Value);
        var admission = await CommitRotatedForReconciliationAsync(
            current.ApiKeyId,
            descriptor,
            ct).ConfigureAwait(false);
        await RetirePreviousVaultReferenceAsync(owner, current, ct)
            .ConfigureAwait(false);
        return Result(
            "rotation_reconciliation_accepted",
            ManagedCodexCredentialActorIdentity.From(owner),
            activeKey.Id,
            activeKey.ExpiresAt.Value,
            admission);
    }

    private async Task<ManagedCodexCredentialDescriptor> ReconcilePolicyAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor current,
        ManagedCodexNyxIdApiKey remote,
        ManagedCodexNyxIdEligibility eligibility,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(eligibility);
        ValidateActiveDescriptor(current, owner, _timeProvider.GetUtcNow());
        var currentApiKeyId = current.ApiKeyId.Trim();
        if (!string.Equals(currentApiKeyId, remote.Id?.Trim(), StringComparison.Ordinal))
        {
            throw Failure(
                "managed_api_key_update_invalid",
                "The managed Codex key selected for policy repair does not match the current credential.");
        }

        if (!HasExactApiKeyPolicy(remote, eligibility))
        {
            await _nyxIdPort.UpdateApiKeyPolicyAsync(
                bearerToken,
                currentApiKeyId,
                PolicyUpdateRequest(eligibility),
                ct).ConfigureAwait(false);
        }

        var keys = await _nyxIdPort.ListApiKeysAsync(bearerToken, ct).ConfigureAwait(false);
        var matches = keys
            .Where(key => string.Equals(key.Id, currentApiKeyId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw Failure(
                "managed_api_key_update_invalid",
                "NyxID did not persist the repaired managed Codex key.");
        }

        var persisted = matches[0];
        ValidatePersistedKey(persisted, eligibility, expectedExpiresAt: null);
        var reference = await RequireCurrentVaultReferenceAsync(
            owner,
            current,
            persisted,
            ct).ConfigureAwait(false);
        var descriptor = BuildDescriptor(
            owner,
            currentApiKeyId,
            reference,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId,
            persisted.ExpiresAt!.Value);
        await CommitPolicyReconciledForReconciliationAsync(
            currentApiKeyId,
            descriptor,
            ct).ConfigureAwait(false);
        return descriptor;
    }

    private async Task<DispatchAdmission> CommitProvisionedForReconciliationAsync(
        ManagedCodexCredentialDescriptor descriptor,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.CommitProvisionedAsync(descriptor, ct)
                .ConfigureAwait(false);
            return admission.Accepted
                ? admission
                : throw PersistencePending();
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            throw PersistencePending();
        }
    }

    private async Task<DispatchAdmission> CommitRotatedForReconciliationAsync(
        string expectedPreviousApiKeyId,
        ManagedCodexCredentialDescriptor descriptor,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.CommitRotatedAsync(
                expectedPreviousApiKeyId,
                descriptor,
                ct).ConfigureAwait(false);
            return admission.Accepted
                ? admission
                : throw PersistencePending();
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            throw PersistencePending();
        }
    }

    private async Task<DispatchAdmission> CommitPolicyReconciledForReconciliationAsync(
        string expectedApiKeyId,
        ManagedCodexCredentialDescriptor descriptor,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.CommitPolicyReconciledAsync(
                expectedApiKeyId,
                descriptor,
                ct).ConfigureAwait(false);
            return admission.Accepted
                ? admission
                : throw PersistencePending();
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            throw PersistencePending();
        }
    }

    private async Task<ManagedCodexNyxIdApiKey> RequirePersistedIssuedKeyAsync(
        string bearerToken,
        string apiKeyId,
        ManagedCodexNyxIdEligibility eligibility,
        DateTimeOffset expectedExpiresAt,
        CancellationToken ct)
    {
        var keys = await _nyxIdPort.ListApiKeysAsync(bearerToken, ct).ConfigureAwait(false);
        var matches = keys
            .Where(key => string.Equals(key.Id, apiKeyId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw Failure(
                "managed_api_key_issue_invalid",
                "NyxID did not persist the issued managed Codex key.");
        }

        ValidatePersistedKey(matches[0], eligibility, expectedExpiresAt);
        return matches[0];
    }

    private void ValidatePersistedKey(
        ManagedCodexNyxIdApiKey key,
        ManagedCodexNyxIdEligibility eligibility,
        DateTimeOffset? expectedExpiresAt)
    {
        ValidateIssuedKey(key, eligibility);
        if (!key.IsActive ||
            key.ExpiresAt is null ||
            key.ExpiresAt.Value <= _timeProvider.GetUtcNow() ||
            expectedExpiresAt.HasValue &&
            key.ExpiresAt.Value.ToUnixTimeMilliseconds() !=
            expectedExpiresAt.Value.ToUnixTimeMilliseconds())
        {
            throw Failure(
                "managed_api_key_expiry_invalid",
                "NyxID did not persist the required finite managed Codex key expiry.");
        }
    }

    private async Task<SecretReference?> TryResolveIssuedReferenceAsync(
        ExternalSubjectRef owner,
        ManagedCodexNyxIdApiKey key,
        CancellationToken ct)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        var secretRef = SecretRefFor(ownerScopeKey, key.Id);
        try
        {
            var resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    secretRef,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    ownerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    "managed-codex-reconcile"),
                ct).ConfigureAwait(false);
            if (!resolved.Resolved ||
                string.IsNullOrWhiteSpace(resolved.Secret) ||
                resolved.Reference is null ||
                !ReferenceMatches(
                    resolved.Reference,
                    secretRef,
                    ownerScopeKey,
                    key.ExpiresAt!.Value))
            {
                return null;
            }
            return resolved.Reference.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Managed Codex Vault reference resolution failed for API key {ApiKeyId}; reconciliation stopped.",
                key.Id);
            throw Failure(
                "managed_credential_vault_unavailable",
                "The managed Codex credential Vault is unavailable; retry reconciliation later.");
        }
    }

    private async Task<SecretReference> RequireCurrentVaultReferenceAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor current,
        ManagedCodexNyxIdApiKey key,
        CancellationToken ct)
    {
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        var currentReference = current.SecretReference;
        var expiresAt = key.ExpiresAt!.Value;
        if (currentReference is null ||
            string.IsNullOrWhiteSpace(currentReference.Ref) ||
            current.ExpiresAt is null ||
            current.ExpiresAt.ToDateTimeOffset().ToUnixTimeMilliseconds() !=
            expiresAt.ToUnixTimeMilliseconds() ||
            !ReferenceMatches(
                currentReference,
                currentReference.Ref,
                ownerScopeKey,
                expiresAt))
        {
            throw Failure(
                "managed_credential_vault_reference_invalid",
                "The current managed Codex credential has an invalid Vault reference.");
        }

        try
        {
            var resolved = await _secretVault.ResolveAsync(
                new ResolveSecretRequest(
                    currentReference.Ref,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    ownerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    "managed-codex-policy-reconcile"),
                ct).ConfigureAwait(false);
            if (!resolved.Resolved ||
                string.IsNullOrWhiteSpace(resolved.Secret) ||
                resolved.Reference is null ||
                !ReferenceMatches(
                    resolved.Reference,
                    currentReference.Ref,
                    ownerScopeKey,
                    expiresAt) ||
                resolved.Reference.Version != currentReference.Version ||
                !string.Equals(
                    resolved.Reference.Fingerprint,
                    currentReference.Fingerprint,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "managed_credential_vault_reference_invalid",
                    "The current managed Codex credential Vault reference could not be validated.");
            }
            return currentReference.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ManagedCodexCredentialLifecycleException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Managed Codex Vault reference validation failed for API key {ApiKeyId}; policy reconciliation stopped.",
                key.Id);
            throw Failure(
                "managed_credential_vault_unavailable",
                "The managed Codex credential Vault is unavailable; retry reconciliation later.");
        }
    }

    private async Task RetirePreviousVaultReferenceAsync(
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor previous,
        CancellationToken ct)
    {
        if (await TryRevokeVaultAsync(
                previous.SecretReference.Ref,
                previous.SecretReference.OwnerScopeKey,
                "managed-codex-rotate-retire-previous",
                ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _ = await _commandPort.QueueCleanupAsync(
                owner,
                new ManagedCodexCredentialCleanup
                {
                    ApiKeyId = previous.ApiKeyId,
                    SecretRef = previous.SecretReference.Ref,
                    NyxIdPending = false,
                    VaultPending = true,
                    RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _logger.LogError(
                "Managed Codex cleanup tracking failed for prior API key {ApiKeyId}; its Vault record will expire with the key.",
                previous.ApiKeyId);
        }
    }

    private static string SecretRefFor(string ownerScopeKey, string apiKeyId)
    {
        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{ownerScopeKey}\n{apiKeyId.Trim()}"));
        return "sec_managed_codex_" + Convert.ToHexStringLower(digest);
    }

    private static bool ReferenceMatches(
        SecretReference reference,
        string expectedRef,
        string expectedOwnerScopeKey,
        DateTimeOffset expectedExpiresAt) =>
        string.Equals(reference.Ref, expectedRef, StringComparison.Ordinal) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
            StringComparison.Ordinal) &&
        string.Equals(reference.OwnerScopeKey, expectedOwnerScopeKey, StringComparison.Ordinal) &&
        reference.Version > 0 &&
        !string.IsNullOrWhiteSpace(reference.Fingerprint) &&
        reference.ExpiresAtUnixMs == expectedExpiresAt.ToUnixTimeMilliseconds();

    private static ManagedCodexCredentialLifecycleException PersistencePending() =>
        Failure(
            "managed_credential_persistence_pending",
            "Managed Codex credential persistence is pending reconciliation.");

    private async Task RetryPendingCleanupAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        IReadOnlyList<ManagedCodexCredentialCleanup>? pendingRevocations,
        CancellationToken ct)
    {
        if (pendingRevocations is null || pendingRevocations.Count == 0)
            return;

        var allCompleted = true;
        var ownerScopeKey = ManagedCodexCredentialActorIdentity.From(owner);
        foreach (var cleanup in pendingRevocations)
        {
            if (string.IsNullOrWhiteSpace(cleanup.ApiKeyId))
            {
                allCompleted = false;
                continue;
            }

            var apiKeyId = cleanup.ApiKeyId.Trim();
            if (cleanup.NyxIdPending)
            {
                var deleted = await TryDeleteNyxIdKeyAsync(bearerToken, apiKeyId, ct)
                    .ConfigureAwait(false);
                if (!deleted || !await CompleteCleanupTrackAsync(
                        owner,
                        apiKeyId,
                        ManagedCodexCredentialCleanupTrack.NyxId,
                        ct).ConfigureAwait(false))
                {
                    allCompleted = false;
                }
            }

            if (cleanup.VaultPending)
            {
                var revoked = !string.IsNullOrWhiteSpace(cleanup.SecretRef) &&
                              await TryRevokeVaultAsync(
                                  cleanup.SecretRef.Trim(),
                                  ownerScopeKey,
                                  "managed-codex-retry-cleanup",
                                  ct).ConfigureAwait(false);
                if (!revoked || !await CompleteCleanupTrackAsync(
                        owner,
                        apiKeyId,
                        ManagedCodexCredentialCleanupTrack.Vault,
                        ct).ConfigureAwait(false))
                {
                    allCompleted = false;
                }
            }
        }

        if (!allCompleted)
        {
            throw Failure(
                "managed_credential_cleanup_pending",
                "Managed Codex credential cleanup is still pending; retry later.");
        }
    }

    private async Task<bool> CompleteCleanupTrackAsync(
        ExternalSubjectRef owner,
        string apiKeyId,
        ManagedCodexCredentialCleanupTrack track,
        CancellationToken ct)
    {
        try
        {
            var admission = await _commandPort.CompleteCleanupTrackAsync(
                owner,
                apiKeyId,
                track,
                _timeProvider.GetUtcNow(),
                ct).ConfigureAwait(false);
            return admission.Accepted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task CompensateUnadoptedAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        string apiKeyId,
        string secretRef,
        string ownerScopeKey,
        CancellationToken ct)
    {
        var nyxIdPending = !await TryDeleteNyxIdKeyAsync(bearerToken, apiKeyId, ct).ConfigureAwait(false);
        var vaultPending = !await TryRevokeVaultAsync(
            secretRef,
            ownerScopeKey,
            "managed-codex-provision-compensation",
            ct).ConfigureAwait(false);
        if (!nyxIdPending && !vaultPending)
            return;

        await _commandPort.QueueCleanupAsync(
            owner,
            new ManagedCodexCredentialCleanup
            {
                ApiKeyId = apiKeyId,
                SecretRef = secretRef,
                NyxIdPending = nyxIdPending,
                VaultPending = vaultPending,
                RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
            },
            ct).ConfigureAwait(false);
    }

    private async Task<bool> CompensateRejectedIssuanceAsync(
        string bearerToken,
        ExternalSubjectRef owner,
        string apiKeyId,
        CancellationToken ct)
    {
        if (await TryDeleteNyxIdKeyAsync(bearerToken, apiKeyId, ct).ConfigureAwait(false))
            return true;

        try
        {
            _ = await _commandPort.QueueCleanupAsync(
                owner,
                new ManagedCodexCredentialCleanup
                {
                    ApiKeyId = apiKeyId,
                    NyxIdPending = true,
                    VaultPending = false,
                    RequestedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            _logger.LogError(
                "Managed Codex cleanup tracking failed for rejected API key {ApiKeyId}; the deterministic key locator remains retryable.",
                apiKeyId);
        }
        return false;
    }

    private async Task<bool> TryDeleteNyxIdKeyAsync(
        string bearerToken,
        string apiKeyId,
        CancellationToken ct)
    {
        try
        {
            return await _nyxIdPort.RevokeApiKeyAsync(bearerToken, apiKeyId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryRevokeVaultAsync(
        string secretRef,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct)
    {
        try
        {
            var result = await _secretVault.RevokeAsync(
                new RevokeSecretRequest(
                    secretRef,
                    CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                    ownerScopeKey,
                    ManagedCodexCredentialActorIdentity.SecretSubjectId,
                    auditReason),
                ct).ConfigureAwait(false);
            return result.Revoked;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
            throw Failure("managed_target_disabled", "Managed Codex execution is disabled.");
    }

    private static ManagedCodexCredentialDescriptor BuildDescriptor(
        ExternalSubjectRef owner,
        string apiKeyId,
        SecretReference reference,
        string chronoSandboxUserServiceId,
        string chronoLlmUserServiceId,
        DateTimeOffset expiresAt) =>
        new()
        {
            Owner = owner.Clone(),
            ApiKeyId = apiKeyId,
            SecretReference = reference.Clone(),
            ChronoSandboxUserServiceId = chronoSandboxUserServiceId,
            ChronoLlmUserServiceId = chronoLlmUserServiceId,
            ChronoSandboxServiceSlug = ManagedCodexOptions.ChronoSandboxServiceSlug,
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt.ToUniversalTime()),
            Status = ManagedCodexCredentialStatus.Active,
        };

    private static SecretReference ValidateStoredReference(
        SecretReference? reference,
        string expectedRef,
        string ownerScopeKey,
        DateTimeOffset expiresAt)
    {
        if (reference is null ||
            !string.Equals(reference.Ref, expectedRef, StringComparison.Ordinal) ||
            !string.Equals(reference.Purpose, CredentialSecretPurposes.ManagedCodexInvocationAgentKey, StringComparison.Ordinal) ||
            !string.Equals(reference.OwnerScopeKey, ownerScopeKey, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reference.Fingerprint) ||
            reference.Version <= 0 ||
            reference.ExpiresAtUnixMs != expiresAt.ToUnixTimeMilliseconds())
        {
            throw Failure("managed_credential_vault_reference_invalid", "The secret vault returned an invalid managed Codex reference.");
        }
        return reference.Clone();
    }

    private static void ValidateActiveDescriptor(
        ManagedCodexCredentialDescriptor credential,
        ExternalSubjectRef owner,
        DateTimeOffset now,
        bool requireFutureExpiry = true)
    {
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        if (credential.Owner is null ||
            !string.Equals(ManagedCodexCredentialActorIdentity.From(credential.Owner), actorId, StringComparison.Ordinal) ||
            credential.SecretReference is null ||
            !string.Equals(credential.SecretReference.Purpose, CredentialSecretPurposes.ManagedCodexInvocationAgentKey, StringComparison.Ordinal) ||
            !string.Equals(credential.SecretReference.OwnerScopeKey, actorId, StringComparison.Ordinal) ||
            credential.SecretReference.Version <= 0 ||
            string.IsNullOrWhiteSpace(credential.SecretReference.Fingerprint) ||
            string.IsNullOrWhiteSpace(credential.ApiKeyId) ||
            credential.Status != ManagedCodexCredentialStatus.Active ||
            credential.ExpiresAt is null ||
            requireFutureExpiry && credential.ExpiresAt.ToDateTimeOffset() <= now)
        {
            throw Failure("managed_credential_invalid", "The projected managed Codex credential is invalid.");
        }
    }

    private static void ValidateIssuedKey(
        ManagedCodexNyxIdApiKey key,
        ManagedCodexNyxIdEligibility eligibility)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(key.Id) ||
            !string.Equals(key.Name, CredentialName, StringComparison.Ordinal) ||
            !HasExactApiKeyPolicy(key, eligibility))
        {
            throw Failure("managed_api_key_issue_invalid", "NyxID returned an invalid or over-broad managed Codex key.");
        }
    }

    private static bool HasExactApiKeyPolicy(
        ManagedCodexNyxIdApiKey key,
        ManagedCodexNyxIdEligibility eligibility) =>
        string.Equals(key.Scopes, "proxy", StringComparison.Ordinal) &&
        string.Equals(key.Platform, "codex", StringComparison.Ordinal) &&
        !key.AllowAllServices &&
        HasExactServiceIds(
            key.AllowedServiceIds,
            eligibility.ChronoSandboxUserServiceId,
            eligibility.ChronoLlmUserServiceId) &&
        !key.AllowAllNodes &&
        key.AllowedNodeIds is { Count: 0 };

    private static bool HasExactServiceIds(
        IReadOnlyList<string> actual,
        string sandboxId,
        string llmId)
    {
        var expected = new HashSet<string>(
            [sandboxId, llmId],
            StringComparer.Ordinal);
        return expected.Count == 2 &&
               actual is not null &&
               actual.Count == expected.Count &&
               actual.All(expected.Contains) &&
               actual.Distinct(StringComparer.Ordinal).Count() == expected.Count;
    }

    private static ManagedCodexCredentialMutationResult Result(
        string status,
        string actorId,
        string apiKeyId,
        DateTimeOffset expiresAt,
        DispatchAdmission admission) =>
        new(status, actorId, apiKeyId, expiresAt.ToUnixTimeMilliseconds(), admission.CommandId);

    private static ManagedCodexCredentialLifecycleException Failure(
        string code,
        string message) =>
        new(code, message);

}
