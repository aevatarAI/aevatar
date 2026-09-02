using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.GAgents.Scheduled;

internal sealed class ScheduledCredentialEffectLifecycle
{
    private readonly ISecretVault _secretVault;
    private readonly IScheduledAgentApiKeyIssuer _apiKeyIssuer;

    public ScheduledCredentialEffectLifecycle(
        ISecretVault secretVault,
        IScheduledAgentApiKeyIssuer apiKeyIssuer)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _apiKeyIssuer = apiKeyIssuer ?? throw new ArgumentNullException(nameof(apiKeyIssuer));
    }

    public async Task<int> ReconcileAsync(
        string token,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        ScheduledCredentialEffectLocator locator,
        CancellationToken ct)
    {
        ScheduledAgentApiKeyLookupResult lookup;
        try
        {
            lookup = await _apiKeyIssuer.FindActiveKeysByNameAsync(
                token,
                validatedPlan,
                locator.CredentialName,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("scheduled_credential_reconciliation_failed", ex);
        }
        if (!lookup.Completed)
            throw new InvalidOperationException("scheduled_credential_reconciliation_failed");
        if (lookup.ActiveApiKeyIds.Count == 0)
            return 0;

        try
        {
            var vaultCleared = false;
            foreach (var apiKeyId in lookup.ActiveApiKeyIds)
            {
                var revoked = await _secretVault.RevokeAsync(
                    BuildRevokeRequest(locator, apiKeyId, "scheduled-credential-recovery"),
                    ct);
                if (revoked.Revoked)
                {
                    vaultCleared = true;
                    break;
                }
            }
            if (!vaultCleared)
                throw new InvalidOperationException("scheduled_credential_reconciliation_failed");

            foreach (var apiKeyId in lookup.ActiveApiKeyIds)
            {
                var revoked = await _apiKeyIssuer.RevokeAsync(token, apiKeyId, ct);
                if (!revoked.Completed)
                    throw new InvalidOperationException("scheduled_credential_reconciliation_failed");
            }
            return lookup.ActiveApiKeyIds.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (
            !string.Equals(ex.Message, "scheduled_credential_reconciliation_failed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("scheduled_credential_reconciliation_failed", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("scheduled_credential_reconciliation_failed", ex);
        }
    }

    public async Task<StoreSecretResult> StoreIssuedSecretAsync(
        ScheduledAgentApiKeyIssueResult issuedKey,
        ScheduledCredentialEffectLocator locator,
        string auditReason,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issuedKey);
        if (!issuedKey.Success || string.IsNullOrWhiteSpace(issuedKey.ApiKeyId))
            throw new InvalidOperationException("Issued scheduled credential is incomplete.");

        var stored = await issuedKey.StoreSecretAsync(
            _secretVault,
            new StoreSecretRequest(
                locator.SecretPurpose,
                locator.SecretOwnerScopeKey,
                issuedKey.ApiKeyId,
                string.Empty,
                auditReason,
                issuedKey.KeyExpiresAtUnixMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(issuedKey.KeyExpiresAtUnixMs)
                    : null,
                locator.RequestedSecretReference),
            ct);
        if (!string.Equals(stored.Reference.Ref, locator.RequestedSecretReference, StringComparison.Ordinal) ||
            !string.Equals(stored.Reference.Purpose, locator.SecretPurpose, StringComparison.Ordinal) ||
            !string.Equals(stored.Reference.OwnerScopeKey, locator.SecretOwnerScopeKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("scheduled_credential_vault_descriptor_mismatch");
        }
        return stored;
    }

    public async Task CleanupIssuedAsync(
        string token,
        string apiKeyId,
        ScheduledCredentialEffectLocator locator,
        CancellationToken ct)
    {
        var vault = await _secretVault.RevokeAsync(
            BuildRevokeRequest(locator, apiKeyId, "scheduled-credential-materialization-cleanup"),
            ct);
        if (!vault.Revoked)
            throw new InvalidOperationException("scheduled_credential_cleanup_failed");

        var nyxId = await _apiKeyIssuer.RevokeAsync(token, apiKeyId, ct);
        if (!nyxId.Completed)
            throw new InvalidOperationException("scheduled_credential_cleanup_failed");
    }

    private static RevokeSecretRequest BuildRevokeRequest(
        ScheduledCredentialEffectLocator locator,
        string apiKeyId,
        string auditReason) =>
        new(
            locator.RequestedSecretReference,
            locator.SecretPurpose,
            locator.SecretOwnerScopeKey,
            apiKeyId,
            auditReason);
}
