using Aevatar.Configuration;
using Aevatar.GAgents.Channel.NyxIdRelay;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class NyxIdRelayRegistrationCredentialResolver : INyxIdRelayRegistrationCredentialResolver
{
    private readonly IChannelBotRegistrationQueryByNyxIdentityPort _queryPort;
    private readonly IAevatarSecretsStore _secretsStore;

    public NyxIdRelayRegistrationCredentialResolver(
        IChannelBotRegistrationQueryByNyxIdentityPort queryPort,
        IAevatarSecretsStore secretsStore)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _secretsStore = secretsStore ?? throw new ArgumentNullException(nameof(secretsStore));
    }

    public async Task<NyxIdRelayRegistrationCredential?> ResolveAsync(string relayApiKeyId, CancellationToken ct = default)
    {
        var normalizedRelayApiKeyId = NormalizeOptional(relayApiKeyId);
        if (normalizedRelayApiKeyId is null)
            return null;

        var registration = await _queryPort.GetByNyxAgentApiKeyIdAsync(normalizedRelayApiKeyId, ct);
        if (registration is null)
            return null;

        var credentialRef = NormalizeOptional(registration.CredentialRef);
        if (credentialRef is null)
            return null;

        var apiKeyHash = NormalizeOptional(_secretsStore.Get(credentialRef));
        if (apiKeyHash is null)
            return null;

        return new NyxIdRelayRegistrationCredential(
            registration.Id ?? string.Empty,
            normalizedRelayApiKeyId,
            apiKeyHash);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
