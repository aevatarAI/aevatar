using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Studio.Application.Authorization;
using Aevatar.Studio.Application.Provisioning;

namespace Aevatar.GAgents.Scheduled;

public sealed class StudioScheduledCredentialMaterializer : IStudioScheduledCredentialMaterializer
{
    private readonly IScheduledAgentCredentialLifecycle _lifecycle;

    public StudioScheduledCredentialMaterializer(IScheduledAgentCredentialLifecycle lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public async Task<StudioScheduledCredential> MaterializeAsync(
        string bearerToken,
        ScheduledInvocationAuthorizationPlan plan,
        string scheduleId,
        OwnerScope ownerScope,
        CancellationToken ct = default)
    {
        var result = await _lifecycle.ProvisionAsync(
            bearerToken,
            plan,
            $"studio-schedule-{scheduleId}",
            scheduleId,
            ownerScope,
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            $"schedule:{scheduleId}",
            "studio-scheduled-invocation-key",
            ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.IssuedKey.ApiKeyId))
            throw new InvalidOperationException(result.IssuedKey.Error ?? "scheduled_credential_materialization_failed");

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(result.IssuedKey.KeyExpiresAtUnixMs);
        return new StudioScheduledCredential(result.IssuedKey.ApiKeyId!, result.SecretReference!, expiresAt);
    }

    public Task RevokeAsync(
        string bearerToken,
        string scheduleId,
        OwnerScope ownerScope,
        StudioScheduledCredential credential,
        CancellationToken ct = default) =>
        _lifecycle.RequestRevocationAsync(
            bearerToken,
            scheduleId,
            credential.ApiKeyId,
            ownerScope,
            credential.SecretReference,
            ct);
}
