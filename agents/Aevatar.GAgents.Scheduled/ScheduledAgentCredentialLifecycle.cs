using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.Scheduled;

public interface IScheduledAgentCredentialRevocationExecutor
{
    Task ExecutePendingAsync(
        string bearerToken,
        UserAgentApiKeyRevocation pending,
        CancellationToken ct = default);
}

public sealed class ScheduledAgentCredentialLifecycle : IScheduledAgentCredentialRevocationExecutor
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
                    new SecretReference
                    {
                        Ref = "sec_" + Guid.NewGuid().ToString("N"),
                        Purpose = purpose,
                        OwnerScopeKey = ownerScopeKey,
                        Version = 1,
                        Fingerprint = "unknown",
                    },
                    ScheduledCredentialRevocationTrackStatus.Completed,
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
                new SecretReference
                {
                    Ref = requestedRef,
                    Purpose = purpose,
                    OwnerScopeKey = ownerScopeKey,
                    Version = 1,
                    Fingerprint = "unknown",
                    ExpiresAtUnixMs = issuedKey.KeyExpiresAtUnixMs,
                },
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

        if (pending.VaultTrack?.Status == ScheduledCredentialRevocationTrackStatus.Pending &&
            pending.NyxApiKeyReference is not null)
        {
            try
            {
                var revoked = await _secretVault.RevokeAsync(new RevokeSecretRequest(
                    pending.NyxApiKeyReference.Ref,
                    pending.NyxApiKeyReference.Purpose,
                    pending.NyxApiKeyReference.OwnerScopeKey,
                    pending.SecretSubjectId,
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
            ScheduledCredentialRevocationTrackStatus.Pending,
            ct);

    private Task RequestRevocationAsync(
        string token,
        string agentId,
        string apiKeyId,
        SecretReference reference,
        ScheduledCredentialRevocationTrackStatus vaultStatus,
        CancellationToken ct) =>
        _catalogCommandPort.RequestCredentialRevocationAsync(new UserAgentApiKeyRevocation
        {
            AgentId = agentId,
            ApiKeyId = apiKeyId,
            NyxApiKeyReference = reference.Clone(),
            SecretSubjectId = apiKeyId,
            RequestedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            RequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            NyxIdTrack = new ScheduledCredentialRevocationTrack
            {
                Status = ScheduledCredentialRevocationTrackStatus.Pending,
            },
            VaultTrack = new ScheduledCredentialRevocationTrack
            {
                Status = vaultStatus,
            },
        }, ct, token);

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
                SecretReferenceRef = pending.NyxApiKeyReference?.Ref ?? string.Empty,
            },
            ct);

    private static UserAgentApiKeyRevocation ToRevocation(UserAgentApiKeyRevocationReadModelEntry pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var revocation = new UserAgentApiKeyRevocation
        {
            AgentId = pending.AgentId,
            ApiKeyId = pending.ApiKeyId,
            SecretSubjectId = pending.SecretSubjectId,
            NyxIdTrack = pending.NyxIdTrack?.Clone(),
            VaultTrack = pending.VaultTrack?.Clone(),
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
