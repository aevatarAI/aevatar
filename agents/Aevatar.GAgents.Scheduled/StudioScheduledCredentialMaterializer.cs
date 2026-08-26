using System.Security.Cryptography;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.GAgents.Scheduled;

public sealed class StudioScheduledCredentialMaterializer : IStudioScheduledCredentialMaterializer
{
    private const int CredentialIdentityDigestBytes = 12;
    private const int NyxIdApiKeyNameMaxUtf8Bytes = 200;
    private const string CredentialNamePrefix = "studio-schedule-";

    private readonly IScheduledAgentApiKeyIssuer _apiKeyIssuer;
    private readonly ISecretVault _secretVault;
    private readonly ScheduledCredentialEffectLifecycle _effects;

    public StudioScheduledCredentialMaterializer(
        IScheduledAgentApiKeyIssuer apiKeyIssuer,
        ISecretVault secretVault)
    {
        _apiKeyIssuer = apiKeyIssuer ?? throw new ArgumentNullException(nameof(apiKeyIssuer));
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _effects = new ScheduledCredentialEffectLifecycle(_secretVault, _apiKeyIssuer);
    }

    public ScheduledCredentialEffectLocator CreateEffectLocator(
        string scheduleId,
        string operationId,
        ScheduledInvocationAuthorizationOwner credentialOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(credentialOwner);
        return new ScheduledCredentialEffectLocator(
            BuildCredentialName(scheduleId, operationId),
            BuildRequestedSecretReference(scheduleId, operationId),
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            $"schedule:{scheduleId}",
            NormalizeCredentialOwner(credentialOwner));
    }

    public async Task<StudioScheduledCredential> MaterializeAsync(
        string bearerToken,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string scheduleId,
        string operationId,
        ScheduledCredentialEffectLocator effectLocator,
        StudioScheduledCredentialMaterializationMode mode,
        OwnerScope ownerScope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(validatedPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(effectLocator);
        if (mode is not (StudioScheduledCredentialMaterializationMode.Initial or
            StudioScheduledCredentialMaterializationMode.Recovery))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        var plan = validatedPlan.Plan ??
            throw new InvalidOperationException("scheduled_authorization_plan_missing");
        var owner = plan.Owner ??
            throw new InvalidOperationException("scheduled_authorization_owner_missing");
        var plannedOwner = new ScheduledInvocationAuthorizationOwner(
            owner.Authority?.Trim() ?? string.Empty,
            owner.OwnerKind.ToString(),
            owner.OwnerSubject?.Trim() ?? string.Empty);
        if (effectLocator != CreateEffectLocator(scheduleId, operationId, plannedOwner))
            throw new InvalidOperationException("scheduled_credential_effect_locator_mismatch");
        var recoveredEffectCount = await _effects.ReconcileAsync(
            bearerToken,
            validatedPlan,
            effectLocator,
            ct);
        if (mode == StudioScheduledCredentialMaterializationMode.Recovery &&
            recoveredEffectCount == 0)
        {
            const string errorCode = "scheduled_credential_recovery_evidence_missing";
            throw new StudioScheduledCredentialMaterializationException(
                errorCode,
                effectsCleaned: false,
                new InvalidOperationException(errorCode),
                recoveryBlocked: true,
                failureCode: errorCode);
        }

        var issued = await _apiKeyIssuer.IssueAsync(
            bearerToken,
            validatedPlan,
            effectLocator.CredentialName,
            ct);
        if (!issued.Success || string.IsNullOrWhiteSpace(issued.ApiKeyId))
        {
            if (!string.IsNullOrWhiteSpace(issued.ApiKeyId))
            {
                var issueFailure = new InvalidOperationException(
                    issued.Error ?? "scheduled_credential_materialization_failed");
                await CleanupIssuedOrThrowAsync(
                    bearerToken,
                    issued.ApiKeyId,
                    effectLocator,
                    issueFailure);
                if (string.Equals(issued.Error, "authorization_plan_changed", StringComparison.Ordinal) &&
                    issued.AuthorizationPlanMismatchReason != ScheduledAuthorizationPlanMismatchReason.Unspecified)
                {
                    throw new StudioMemberAutomationPlanConflictException(
                        "authorization_plan_changed",
                        "authorization_plan_changed",
                        issued.AuthorizationPlanMismatchReason);
                }

                throw new StudioScheduledCredentialMaterializationException(
                    issueFailure.Message,
                    effectsCleaned: true,
                    issueFailure);
            }
            if (string.Equals(issued.Error, "authorization_plan_changed", StringComparison.Ordinal) &&
                issued.AuthorizationPlanMismatchReason != ScheduledAuthorizationPlanMismatchReason.Unspecified)
            {
                throw new StudioMemberAutomationPlanConflictException(
                    "authorization_plan_changed",
                    "authorization_plan_changed",
                    issued.AuthorizationPlanMismatchReason);
            }

            var failureCode = issued.Error ?? "scheduled_credential_materialization_failed";
            throw new StudioScheduledCredentialMaterializationException(
                failureCode,
                effectsCleaned: true,
                new InvalidOperationException(failureCode),
                failureCode: failureCode);
        }

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(issued.KeyExpiresAtUnixMs);
        try
        {
            var stored = await _effects.StoreIssuedSecretAsync(
                issued,
                effectLocator,
                "studio-scheduled-invocation-key",
                ct);
            return new StudioScheduledCredential(
                issued.ApiKeyId!,
                stored.Reference,
                expiresAt,
                plannedOwner,
                issued.DurableOperationGrants);
        }
        catch (Exception ex)
        {
            await CleanupIssuedOrThrowAsync(bearerToken, issued.ApiKeyId!, effectLocator, ex);
            throw new StudioScheduledCredentialMaterializationException(
                ex.Message,
                effectsCleaned: true,
                ex);
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

    private static ScheduledInvocationAuthorizationOwner NormalizeCredentialOwner(
        ScheduledInvocationAuthorizationOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new ScheduledInvocationAuthorizationOwner(
            NormalizeRequired(owner.Authority, nameof(owner.Authority)),
            NormalizeRequired(owner.OwnerKind, nameof(owner.OwnerKind)),
            NormalizeRequired(owner.OwnerSubject, nameof(owner.OwnerSubject)));
    }

    internal static string BuildCredentialName(string scheduleId, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var name = CredentialNamePrefix +
                   ComputeIdentityDigest(scheduleId) + "-" +
                   ComputeIdentityDigest(operationId);
        if (Encoding.UTF8.GetByteCount(name) > NyxIdApiKeyNameMaxUtf8Bytes)
            throw new InvalidOperationException("scheduled_credential_name_too_long");
        return name;
    }

    internal static string BuildRequestedSecretReference(string scheduleId, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return "sec_studio_schedule_" + ComputeIdentityDigest(scheduleId + "\0" + operationId);
    }

    private async Task CleanupIssuedOrThrowAsync(
        string bearerToken,
        string apiKeyId,
        ScheduledCredentialEffectLocator effectLocator,
        Exception? materializationFailure)
    {
        try
        {
            await _effects.CleanupIssuedAsync(
                bearerToken,
                apiKeyId,
                effectLocator,
                CancellationToken.None);
            return;
        }
        catch (Exception cleanupFailure)
        {
            throw new InvalidOperationException(
                "scheduled_credential_cleanup_failed",
                new AggregateException(
                    new[] { materializationFailure, cleanupFailure }
                        .Where(static failure => failure != null)
                        .Cast<Exception>()));
        }

    }

    private static string ComputeIdentityDigest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))
                .AsSpan(0, CredentialIdentityDigestBytes))
            .ToLowerInvariant();

    private static string NormalizeRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        return value.Trim();
    }
}
