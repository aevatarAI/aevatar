using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.GAgents.Scheduled;

public sealed class StudioScheduledCredentialMaterializer : IStudioScheduledCredentialMaterializer
{
    private readonly IScheduledAgentApiKeyIssuer _apiKeyIssuer;
    private readonly ISecretVault _secretVault;

    public StudioScheduledCredentialMaterializer(
        IScheduledAgentApiKeyIssuer apiKeyIssuer,
        ISecretVault secretVault)
    {
        _apiKeyIssuer = apiKeyIssuer ?? throw new ArgumentNullException(nameof(apiKeyIssuer));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
    }

    public async Task<StudioScheduledCredential> MaterializeAsync(
        string bearerToken,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string scheduleId,
        OwnerScope ownerScope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(validatedPlan);
        var plan = validatedPlan.Plan ??
            throw new InvalidOperationException("scheduled_authorization_plan_missing");
        var owner = plan.Owner ??
            throw new InvalidOperationException("scheduled_authorization_owner_missing");
        var issued = await _apiKeyIssuer.IssueAsync(
            bearerToken,
            validatedPlan,
            $"studio-schedule-{scheduleId}",
            ct);
        if (!issued.Success || string.IsNullOrWhiteSpace(issued.ApiKeyId))
        {
            if (!string.IsNullOrWhiteSpace(issued.ApiKeyId))
                _ = await _apiKeyIssuer.RevokeAsync(bearerToken, issued.ApiKeyId, CancellationToken.None);
            throw new InvalidOperationException(issued.Error ?? "scheduled_credential_materialization_failed");
        }

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(issued.KeyExpiresAtUnixMs);
        var requestedRef = "sec_" + Guid.NewGuid().ToString("N");
        try
        {
            var stored = await issued.StoreSecretAsync(
                _secretVault,
                new StoreSecretRequest(
                    CredentialSecretPurposes.ScheduledInvocationAgentKey,
                    $"schedule:{scheduleId}",
                    issued.ApiKeyId!,
                    string.Empty,
                    "studio-scheduled-invocation-key",
                    expiresAt,
                    requestedRef),
                ct);
            return new StudioScheduledCredential(
                issued.ApiKeyId!,
                stored.Reference,
                expiresAt,
                new ScheduledInvocationAuthorizationOwner(
                    owner.Authority,
                    owner.OwnerKind.ToString(),
                    owner.OwnerSubject));
        }
        catch
        {
            _ = await _apiKeyIssuer.RevokeAsync(bearerToken, issued.ApiKeyId!, CancellationToken.None);
            throw;
        }
    }

    public async Task<StudioScheduledCredentialRevocationResult> RevokeAsync(
        string bearerToken,
        AuthenticatedAuthorizationOwnerContext authenticatedOwner,
        StudioScheduledCredential credential,
        bool revokeNyxId,
        bool revokeVault,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedOwner);
        ArgumentNullException.ThrowIfNull(credential);
        EnsureSameOwner(authenticatedOwner, credential.Owner);

        var nyxIdRevoked = !revokeNyxId;
        var vaultRevoked = !revokeVault;
        var errorCode = string.Empty;
        if (revokeNyxId)
        {
            var result = await _apiKeyIssuer.RevokeAsync(bearerToken, credential.ApiKeyId, ct);
            nyxIdRevoked = result.Completed;
            if (!result.Completed)
                errorCode = "nyxid_revocation_" + result.FailureKind.ToString().ToLowerInvariant();
        }

        if (revokeVault)
        {
            try
            {
                var reference = credential.SecretReference;
                var result = await _secretVault.RevokeAsync(new RevokeSecretRequest(
                    reference.Ref,
                    reference.Purpose,
                    reference.OwnerScopeKey,
                    credential.ApiKeyId,
                    "team-automation-credential-revocation"), ct);
                vaultRevoked = result.Revoked;
                if (!result.Revoked && errorCode.Length == 0)
                    errorCode = "vault_revocation_rejected";
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                vaultRevoked = false;
                if (errorCode.Length == 0)
                    errorCode = "vault_revocation_transient";
            }
        }

        return new StudioScheduledCredentialRevocationResult(
            nyxIdRevoked,
            vaultRevoked,
            nyxIdRevoked && vaultRevoked ? string.Empty : errorCode);
    }

    private static void EnsureSameOwner(
        AuthenticatedAuthorizationOwnerContext authenticated,
        ScheduledInvocationAuthorizationOwner credentialOwner)
    {
        var owner = authenticated.Owner ??
            throw new UnauthorizedAccessException("authenticated_authorization_owner_missing");
        if (!string.Equals(owner.Authority?.Trim(), credentialOwner.Authority, StringComparison.Ordinal) ||
            !string.Equals(owner.OwnerKind.ToString(), credentialOwner.OwnerKind, StringComparison.Ordinal) ||
            !string.Equals(owner.OwnerSubject?.Trim(), credentialOwner.OwnerSubject, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("credential_owner_mismatch");
        }
    }
}
