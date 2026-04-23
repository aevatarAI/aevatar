using Aevatar.GAgents.Channel.NyxIdRelay;

namespace Aevatar.GAgents.ChannelRuntime;

internal sealed class NyxIdRelayRegistrationCredentialResolver : INyxIdRelayRegistrationCredentialResolver
{
    private readonly IChannelBotRegistrationQueryByNyxIdentityPort _queryPort;

    public NyxIdRelayRegistrationCredentialResolver(IChannelBotRegistrationQueryByNyxIdentityPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public async Task<NyxIdRelayRegistrationCredential?> ResolveAsync(string relayApiKeyId, CancellationToken ct = default)
    {
        var normalizedRelayApiKeyId = NormalizeOptional(relayApiKeyId);
        if (normalizedRelayApiKeyId is null)
            return null;

        var registration = await _queryPort.GetByNyxAgentApiKeyIdAsync(normalizedRelayApiKeyId, ct);
        if (registration is null)
            return null;

        var apiKeyHash = NormalizeOptional(registration.NyxAgentApiKeyHash);
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
