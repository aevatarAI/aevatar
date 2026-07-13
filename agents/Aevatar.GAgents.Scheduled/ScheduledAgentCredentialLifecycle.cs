using Aevatar.Foundation.Abstractions.Credentials;

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
        ScheduledAgentServiceSlugs serviceSlugs,
        string agentId,
        string skillName,
        string? scopeId,
        string purpose,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct = default);

    Task ExecutePendingAsync(
        string token,
        UserAgentApiKeyRevocationReadModelEntry pending,
        CancellationToken ct = default);

    Task RequestRevocationAsync(
        string token,
        string agentId,
        string apiKeyId,
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

    public ScheduledAgentCredentialLifecycle(
        ISecretVault secretVault,
        IUserAgentCatalogCommandPort catalogCommandPort,
        IScheduledAgentApiKeyIssuer apiKeyIssuer)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
        _apiKeyIssuer = apiKeyIssuer ?? throw new ArgumentNullException(nameof(apiKeyIssuer));
    }

    public async Task<ScheduledAgentCredentialProvisionResult> ProvisionAsync(
        string token,
        ScheduledAgentServiceSlugs serviceSlugs,
        string agentId,
        string skillName,
        string? scopeId,
        string purpose,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct = default)
    {
        var issuedKey = await _apiKeyIssuer.IssueAsync(token, serviceSlugs, agentId, skillName, scopeId, ct);
        if (!issuedKey.Success)
        {
            if (!string.IsNullOrWhiteSpace(issuedKey.ApiKeyId))
            {
                await RequestRevocationAsync(
                    token,
                    agentId,
                    issuedKey.ApiKeyId,
                    null,
                    new ScheduledCredentialVaultRevocationDescriptor
                    {
                        SubjectId = issuedKey.ApiKeyId,
                        ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.NotApplicable,
                    },
                    ScheduledCredentialRevocationTrackStatus.NotApplicable,
                    CancellationToken.None);
            }
            return new ScheduledAgentCredentialProvisionResult(issuedKey, null);
        }

        var reference = await StoreIssuedSecretAsync(
            token,
            issuedKey,
            agentId,
            purpose,
            ownerScopeKey,
            auditReason,
            ct);
        return new ScheduledAgentCredentialProvisionResult(issuedKey, reference);
    }

    private async Task<SecretReference> StoreIssuedSecretAsync(
        string token,
        ScheduledAgentApiKeyIssueResult issuedKey,
        string agentId,
        string purpose,
        string ownerScopeKey,
        string auditReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(issuedKey);
        if (!issuedKey.Success || string.IsNullOrWhiteSpace(issuedKey.ApiKeyId))
            throw new InvalidOperationException("Issued scheduled credential is incomplete.");

        var requestedRef = "sec_" + Guid.NewGuid().ToString("N");
        try
        {
            var stored = await issuedKey.StoreSecretAsync(
                _secretVault,
                new StoreSecretRequest(
                    purpose,
                    ownerScopeKey,
                    issuedKey.ApiKeyId!,
                    string.Empty,
                    auditReason,
                    issuedKey.KeyExpiresAtUnixMs > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(issuedKey.KeyExpiresAtUnixMs)
                        : null,
                    requestedRef),
                ct);
            return stored.Reference;
        }
        catch
        {
            await RequestRevocationAsync(
                token,
                agentId,
                issuedKey.ApiKeyId!,
                null,
                new ScheduledCredentialVaultRevocationDescriptor
                {
                    Ref = requestedRef,
                    Purpose = purpose,
                    OwnerScopeKey = ownerScopeKey,
                    SubjectId = issuedKey.ApiKeyId,
                    ReferenceAvailability = ScheduledCredentialVaultReferenceAvailability.RequestedNotConfirmed,
                },
                ScheduledCredentialRevocationTrackStatus.Pending,
                CancellationToken.None);
            throw;
        }
    }

    public async Task ExecutePendingAsync(
        string token,
        UserAgentApiKeyRevocationReadModelEntry pending,
        CancellationToken ct = default) =>
        await ExecutePendingAsync(token, ToRevocation(pending), ct);

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
        SecretReference reference,
        CancellationToken ct = default) =>
        RequestRevocationAsync(
            token,
            agentId,
            apiKeyId,
            reference,
            ConfirmedDescriptor(reference, apiKeyId),
            ScheduledCredentialRevocationTrackStatus.Pending,
            ct);

    private Task RequestRevocationAsync(
        string token,
        string agentId,
        string apiKeyId,
        SecretReference? reference,
        ScheduledCredentialVaultRevocationDescriptor vaultDescriptor,
        ScheduledCredentialRevocationTrackStatus vaultStatus,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var revocation = new UserAgentApiKeyRevocation
        {
            AgentId = agentId,
            ApiKeyId = apiKeyId,
            SecretSubjectId = apiKeyId,
            VaultRevocationDescriptor = vaultDescriptor.Clone(),
            RequestedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now),
            RequestedAtUnixMs = now.ToUnixTimeMilliseconds(),
            NyxIdTrack = new ScheduledCredentialRevocationTrack
            {
                Status = ScheduledCredentialRevocationTrackStatus.Pending,
            },
            VaultTrack = new ScheduledCredentialRevocationTrack
            {
                Status = vaultStatus,
            },
        };
        if (reference is not null)
            revocation.NyxApiKeyReference = reference.Clone();

        return _catalogCommandPort.RequestCredentialRevocationAsync(revocation, ct, token);
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
                SecretReferenceRef = ResolveSecretReferenceRef(pending),
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

    private static string ResolveSecretReferenceRef(UserAgentApiKeyRevocation pending) =>
        pending.NyxApiKeyReference?.Ref?.Trim() ??
        pending.VaultRevocationDescriptor?.Ref?.Trim() ??
        string.Empty;

    private static UserAgentApiKeyRevocation ToRevocation(UserAgentApiKeyRevocationReadModelEntry pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var revocation = new UserAgentApiKeyRevocation
        {
            AgentId = pending.AgentId,
            ApiKeyId = pending.ApiKeyId,
            SecretSubjectId = pending.SecretSubjectId,
            RepairReason = pending.RepairReason,
            RequestedBySubjectId = pending.RequestedBySubjectId,
            RequestedAtUnixMs = pending.RequestedAtUnixMs,
            NyxIdTrack = pending.NyxIdTrack?.Clone(),
            VaultTrack = pending.VaultTrack?.Clone(),
            VaultRevocationDescriptor = pending.VaultRevocationDescriptor?.Clone(),
        };
        if (pending.NyxApiKeyReference is not null)
            revocation.NyxApiKeyReference = pending.NyxApiKeyReference.Clone();
        if (pending.OwnerScope is not null)
            revocation.OwnerScope = pending.OwnerScope.Clone();
        return revocation;
    }
}

public sealed record ScheduledAgentCredentialProvisionResult(
    ScheduledAgentApiKeyIssueResult IssuedKey,
    SecretReference? SecretReference)
{
    public bool Success => IssuedKey.Success && SecretReference is not null;
}
