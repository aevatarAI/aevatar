using Aevatar.GAgentService.Abstractions;

namespace Aevatar.GAgentService.Core.Ports;

public interface INyxIdRegistrationTokenAccessor
{
    Task<NyxIdRegistrationToken?> GetTokenAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}

public sealed record NyxIdRegistrationToken(
    string OwnerAccessToken,
    string ServiceCredential,
    string CredentialKid);
