using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions.Schedules;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioScheduledCredentialMaterializer
{
    ScheduledCredentialEffectLocator CreateEffectLocator(
        string scheduleId,
        string operationId,
        ScheduledInvocationAuthorizationOwner credentialOwner);

    Task<StudioScheduledCredential> MaterializeAsync(
        string bearerToken,
        ValidatedScheduledInvocationAuthorizationPlan validatedPlan,
        string scheduleId,
        string operationId,
        ScheduledCredentialEffectLocator effectLocator,
        StudioScheduledCredentialMaterializationMode mode,
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

public enum StudioScheduledCredentialMaterializationMode
{
    Initial = 1,
    Recovery = 2,
}

public sealed record StudioScheduledCredential(
    string ApiKeyId,
    SecretReference SecretReference,
    DateTimeOffset ExpiresAtUtc,
    ScheduledInvocationAuthorizationOwner Owner,
    IReadOnlyList<NyxIdDurableOperationGrantRef>? DurableOperationGrants = null);

public sealed record StudioScheduledCredentialRevocationResult(
    bool NyxIdRevoked,
    bool VaultRevoked,
    string ErrorCode);

public sealed class StudioScheduledCredentialMaterializationException : InvalidOperationException
{
    public StudioScheduledCredentialMaterializationException(
        string message,
        bool effectsCleaned,
        Exception innerException,
        bool recoveryBlocked = false,
        string? failureCode = null)
        : base(message, innerException)
    {
        EffectsCleaned = effectsCleaned;
        RecoveryBlocked = recoveryBlocked;
        FailureCode = string.IsNullOrWhiteSpace(failureCode)
            ? string.Empty
            : failureCode.Trim();
    }

    public bool EffectsCleaned { get; }

    public bool RecoveryBlocked { get; }

    public string FailureCode { get; }
}
