using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Studio.Application.Authorization;

namespace Aevatar.Studio.Application.Provisioning;

public interface IStudioScheduledCredentialMaterializer
{
    Task<StudioScheduledCredential> MaterializeAsync(
        string bearerToken,
        ScheduledInvocationAuthorizationPlan plan,
        string scheduleId,
        OwnerScope ownerScope,
        CancellationToken ct = default);

    Task RevokeAsync(
        string bearerToken,
        string scheduleId,
        OwnerScope ownerScope,
        StudioScheduledCredential credential,
        CancellationToken ct = default);
}

public sealed record StudioScheduledCredential(
    string ApiKeyId,
    SecretReference SecretReference,
    DateTimeOffset ExpiresAtUtc);
