using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.GAgents.Scheduled;

public interface IScheduledAgentCredentialRevocationExecutor
{
    Task ExecutePendingAsync(
        string bearerToken,
        UserAgentApiKeyRevocation pending,
        CancellationToken ct = default);
}

public interface IScheduledAgentCredentialLifecycle
{
    Task<ScheduledAgentCredentialProvisionResult> ProvisionAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string credentialName,
        string agentId,
        OwnerScope ownerScope,
        string purpose,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct = default);

    Task RequestRevocationAsync(
        string token,
        string agentId,
        string apiKeyId,
        OwnerScope ownerScope,
        SecretReference reference,
        CancellationToken ct = default);
}

public sealed class ScheduledAgentCredentialLifecycle
    : IScheduledAgentCredentialLifecycle,
      IScheduledAgentCredentialRevocationExecutor
{
    private readonly ISecretVault _secretVault;
    private readonly IUserAgentCatalogCommandPort _catalogCommandPort;
    private readonly IScheduledAgentApiKeyIssuer _apiKeyIssuer;
    private readonly ScheduledCredentialEffectLifecycle _effects;

    public ScheduledAgentCredentialLifecycle(
        ISecretVault secretVault,
        IUserAgentCatalogCommandPort catalogCommandPort,
        IScheduledAgentApiKeyIssuer apiKeyIssuer)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _apiKeyIssuer = apiKeyIssuer ?? throw new ArgumentNullException(nameof(apiKeyIssuer));
        _effects = new ScheduledCredentialEffectLifecycle(_secretVault, _apiKeyIssuer);
    }

    public async Task<ScheduledAgentCredentialProvisionResult> ProvisionAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string credentialName,
        string agentId,
        OwnerScope ownerScope,
        string purpose,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(validatedPlan);
        var issuedKey = await _apiKeyIssuer.IssueAsync(token, validatedPlan, credentialName, ct);
        return await CompleteProvisionAsync(
            token, validatedPlan, issuedKey, credentialName, agentId, ownerScope, purpose, ownerScopeKey, auditReason, ct);
    }

    private async Task<ScheduledAgentCredentialProvisionResult> CompleteProvisionAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        ScheduledAgentApiKeyIssueResult issuedKey,
        string credentialName,
        string agentId,
        OwnerScope ownerScope,
        string purpose,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct)
    {
        if (!issuedKey.Success)
        {
            if (!string.IsNullOrWhiteSpace(issuedKey.ApiKeyId))
            {
                await RequestRevocationAsync(
                    token,
                    agentId,
                    issuedKey.ApiKeyId,
                    ownerScope,
                    null,
                    new ScheduledCredentialVaultRevocationDescriptor
                    {
                        SubjectId = issuedKey.ApiKeyId,
                        ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.NotApplicable,
                    },
                    CancellationToken.None);
            }
            return new ScheduledAgentCredentialProvisionResult(issuedKey, null);
        }

        var reference = await StoreIssuedSecretAsync(
            token,
            issuedKey,
            credentialName,
            agentId,
            ownerScope,
            purpose,
            ownerScopeKey,
            auditReason,
            ResolveCredentialOwner(validatedPlan),
            ct);
        return new ScheduledAgentCredentialProvisionResult(issuedKey, reference);
    }

    private async Task<SecretReference> StoreIssuedSecretAsync(
        string token,
        ScheduledAgentApiKeyIssueResult issuedKey,
        string credentialName,
        string agentId,
        OwnerScope ownerScope,
        string purpose,
        string ownerScopeKey,
        string auditReason,
        ScheduledInvocationAuthorizationOwner credentialOwner,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(issuedKey);
        if (!issuedKey.Success || string.IsNullOrWhiteSpace(issuedKey.ApiKeyId))
            throw new InvalidOperationException("Issued scheduled credential is incomplete.");

        var requestedRef = "sec_" + Guid.NewGuid().ToString("N");
        try
        {
            var stored = await _effects.StoreIssuedSecretAsync(
                issuedKey,
                new ScheduledCredentialEffectLocator(
                    credentialName,
                    requestedRef,
                    purpose,
                    ownerScopeKey,
                    credentialOwner),
                auditReason,
                ct);
            return stored.Reference;
        }
        catch
        {
            await RequestRevocationAsync(
                token,
                agentId,
                issuedKey.ApiKeyId!,
                ownerScope,
                null,
                new ScheduledCredentialVaultRevocationDescriptor
                {
                    Ref = requestedRef,
                    Purpose = purpose,
                    OwnerScopeKey = ownerScopeKey,
                    SubjectId = issuedKey.ApiKeyId,
                    ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.RequestedNotConfirmed,
                },
                CancellationToken.None);
            throw;
        }
    }

    public async Task ExecutePendingAsync(
        string bearerToken,
        UserAgentApiKeyRevocation pending,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.NyxIdTrack?.Status == ScheduledCredentialRevocationTrackStatus.Pending)
        {
            try
            {
                var result = await _apiKeyIssuer.RevokeAsync(bearerToken, pending.ApiKeyId, ct);
                await RecordTrackAsync(
                    pending,
                    UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId,
                    result.Completed,
                    result.HttpStatus,
                    result.Error,
                    result.FailureKind,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordTrackAsync(
                    pending,
                    UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.NyxId,
                    false,
                    0,
                    ex.Message,
                    UserAgentApiKeyRevocationFailureKind.Transient,
                    ct);
            }
        }

        var vaultDescriptor = ResolveVaultRevocationDescriptor(pending);
        if (pending.VaultTrack?.Status == ScheduledCredentialRevocationTrackStatus.Pending &&
            vaultDescriptor is not null)
        {
            try
            {
                var revoked = await _secretVault.RevokeAsync(new RevokeSecretRequest(
                    vaultDescriptor.Ref,
                    vaultDescriptor.Purpose,
                    vaultDescriptor.OwnerScopeKey,
                    vaultDescriptor.SubjectId,
                    "scheduled-credential-revocation"), ct);
                await RecordTrackAsync(
                    pending,
                    UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault,
                    revoked.Revoked,
                    0,
                    revoked.Revoked ? string.Empty : "secret_reference_not_active",
                    revoked.Revoked
                        ? UserAgentApiKeyRevocationFailureKind.None
                        : UserAgentApiKeyRevocationFailureKind.ProviderError,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordTrackAsync(
                    pending,
                    UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track.Vault,
                    false,
                    0,
                    ex.Message,
                    UserAgentApiKeyRevocationFailureKind.Transient,
                    ct);
            }
        }
    }

    public Task RequestRevocationAsync(
        string token,
        string agentId,
        string apiKeyId,
        OwnerScope ownerScope,
        SecretReference reference,
        CancellationToken ct = default) =>
        RequestRevocationAsync(
            token,
            agentId,
            apiKeyId,
            ownerScope,
            reference,
            ConfirmedDescriptor(reference, apiKeyId),
            ct);

    private Task RequestRevocationAsync(
        string token,
        string agentId,
        string apiKeyId,
        OwnerScope ownerScope,
        SecretReference? reference,
        ScheduledCredentialVaultRevocationDescriptor vaultDescriptor,
        CancellationToken ct)
    {
        var intent = new ScheduledAgentCredentialRevocationIntent
        {
            AgentId = agentId,
            ApiKeyId = apiKeyId,
            OwnerScope = ownerScope.Clone(),
            VaultRevocationDescriptor = vaultDescriptor.Clone(),
        };
        if (reference is not null)
            intent.NyxApiKeyReference = reference.Clone();

        return _catalogCommandPort.RequestCredentialRevocationAsync(intent, ct, token);
    }

    private Task RecordTrackAsync(
        UserAgentApiKeyRevocation pending,
        UserAgentCatalogRecordApiKeyRevocationAttemptCommand.Types.Track track,
        bool completed,
        int httpStatus,
        string error,
        UserAgentApiKeyRevocationFailureKind failureKind,
        CancellationToken ct) =>
        _catalogCommandPort.RecordApiKeyRevocationAttemptAsync(
            new UserAgentCatalogRecordApiKeyRevocationAttemptCommand
            {
                AgentId = pending.AgentId,
                ApiKeyId = pending.ApiKeyId,
                Completed = completed,
                HttpStatus = httpStatus,
                Error = error ?? string.Empty,
                FailureKind = failureKind,
                Track = track,
                SecretReferenceRef = ScheduledAgentCredentialRevocationIdentity.ResolveSecretReferenceRef(pending),
            },
            ct);

    private static ScheduledCredentialVaultRevocationDescriptor? ResolveVaultRevocationDescriptor(
        UserAgentApiKeyRevocation pending)
    {
        if (pending.NyxApiKeyReference is not null)
            return ConfirmedDescriptor(pending.NyxApiKeyReference, pending.SecretSubjectId);

        var descriptor = pending.VaultRevocationDescriptor;
        return IsExecutableDescriptor(descriptor) ? descriptor.Clone() : null;
    }

    private static ScheduledCredentialVaultRevocationDescriptor ConfirmedDescriptor(
        SecretReference reference,
        string subjectId) =>
        new()
        {
            Ref = reference.Ref,
            Purpose = reference.Purpose,
            OwnerScopeKey = reference.OwnerScopeKey,
            SubjectId = subjectId,
            ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.Confirmed,
        };

    private static bool IsExecutableDescriptor(ScheduledCredentialVaultRevocationDescriptor? descriptor) =>
        descriptor is not null &&
        descriptor.ReferenceAvailability is ScheduledCredentialVaultReferenceAvailability.RequestedNotConfirmed or
            ScheduledCredentialVaultReferenceAvailability.Confirmed &&
        !string.IsNullOrWhiteSpace(descriptor.Ref) &&
        !string.IsNullOrWhiteSpace(descriptor.Purpose) &&
        !string.IsNullOrWhiteSpace(descriptor.OwnerScopeKey) &&
        !string.IsNullOrWhiteSpace(descriptor.SubjectId);

    private static string ToNyxIdRevocationErrorCode(UserAgentApiKeyRevocationFailureKind failureKind) =>
        failureKind switch
        {
            UserAgentApiKeyRevocationFailureKind.Unauthorized => "nyxid_revocation_unauthorized",
            UserAgentApiKeyRevocationFailureKind.Transient => "nyxid_revocation_transient",
            _ => "nyxid_revocation_provider_error",
        };

    private static ScheduledInvocationAuthorizationOwner ResolveCredentialOwner(
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan)
    {
        var owner = validatedPlan.Plan?.Owner ??
            throw new InvalidOperationException("scheduled_authorization_owner_missing");
        return new ScheduledInvocationAuthorizationOwner(
            owner.Authority?.Trim() ?? string.Empty,
            owner.OwnerKind.ToString(),
            owner.OwnerSubject?.Trim() ?? string.Empty);
    }

}

public sealed record ScheduledAgentCredentialProvisionResult(
    ScheduledAgentApiKeyIssueResult IssuedKey,
    SecretReference? SecretReference)
{
    public bool Success => IssuedKey.Success && SecretReference is not null;
}
