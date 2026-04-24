using Aevatar.GAgents.NyxidChat;

namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Production implementation of <see cref="INyxIdRelayScopeResolver"/> backed by
/// the channel bot registration query port.
/// </summary>
internal sealed class NyxIdRelayScopeResolver : INyxIdRelayScopeResolver
{
    private readonly IChannelBotRegistrationQueryByNyxIdentityPort _registrationQueryPort;

    public NyxIdRelayScopeResolver(IChannelBotRegistrationQueryByNyxIdentityPort registrationQueryPort)
    {
        _registrationQueryPort = registrationQueryPort
            ?? throw new ArgumentNullException(nameof(registrationQueryPort));
    }

    public async Task<string?> ResolveScopeIdByApiKeyAsync(string nyxAgentApiKeyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nyxAgentApiKeyId))
            return null;

        var entry = await _registrationQueryPort.GetByNyxAgentApiKeyIdAsync(nyxAgentApiKeyId.Trim(), ct);
        var scopeId = entry?.ScopeId;
        return string.IsNullOrWhiteSpace(scopeId) ? null : scopeId;
    }
}
