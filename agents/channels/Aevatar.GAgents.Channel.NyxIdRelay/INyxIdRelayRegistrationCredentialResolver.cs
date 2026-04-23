namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record NyxIdRelayRegistrationCredential(
    string RegistrationId,
    string RelayApiKeyId,
    string RelayApiKeyHash);

public interface INyxIdRelayRegistrationCredentialResolver
{
    Task<NyxIdRelayRegistrationCredential?> ResolveAsync(string relayApiKeyId, CancellationToken ct = default);
}
