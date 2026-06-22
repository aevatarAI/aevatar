namespace Aevatar.GAgentService.Core.Ports;

public interface INyxIdRegistrationTokenAccessor
{
    Task<NyxIdRegistrationToken?> GetTokenAsync(CancellationToken ct = default);
}

public sealed record NyxIdRegistrationToken(
    string AccessToken,
    string CredentialKid);
