using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioScheduledCredentialMaterializer
{
    Task<StudioScheduledCredential> MaterializeAsync(
        string bearerToken,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string scheduleId,
        OwnerScope ownerScope,
        CancellationToken ct = default);

    Task<StudioScheduledCredentialRevocationResult> RevokeAsync(
        string bearerToken,
        AuthenticatedAuthorizationOwnerContext authenticatedOwner,
        StudioScheduledCredential credential,
        bool revokeNyxId,
        bool revokeVault,
        CancellationToken ct = default);
}

public sealed record StudioScheduledCredential(
    string ApiKeyId,
    SecretReference SecretReference,
    DateTimeOffset ExpiresAtUtc,
    ScheduledInvocationAuthorizationOwner Owner);

public sealed record StudioScheduledCredentialRevocationResult(
    bool NyxIdRevoked,
    bool VaultRevoked,
    string ErrorCode);
