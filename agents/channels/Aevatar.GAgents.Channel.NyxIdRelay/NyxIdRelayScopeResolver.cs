using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Production implementation of <see cref="INyxIdRelayScopeResolver"/> backed by
/// the channel bot registration query port.
/// </summary>
internal sealed class NyxIdRelayScopeResolver : INyxIdRelayScopeResolver
{
    private readonly IChannelBotRegistrationQueryByNyxIdentityPort _registrationQueryPort;
    private readonly ILogger<NyxIdRelayScopeResolver> _logger;

    public NyxIdRelayScopeResolver(
        IChannelBotRegistrationQueryByNyxIdentityPort registrationQueryPort,
        ILogger<NyxIdRelayScopeResolver>? logger = null)
    {
        _registrationQueryPort = registrationQueryPort
            ?? throw new ArgumentNullException(nameof(registrationQueryPort));
        _logger = logger ?? NullLogger<NyxIdRelayScopeResolver>.Instance;
    }

    public async Task<string?> ResolveScopeIdByApiKeyAsync(string nyxAgentApiKeyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nyxAgentApiKeyId))
            return null;

        var trimmedApiKeyId = nyxAgentApiKeyId.Trim();
        var entries = await _registrationQueryPort.ListByNyxAgentApiKeyIdAsync(trimmedApiKeyId, ct);

        var active = entries.Where(entry => !entry.Tombstoned).ToArray();
        if (active.Length != 1)
        {
            if (active.Length > 1)
                _logger.LogWarning(
                    "Refusing ambiguous relay registration: apiKeyId={ApiKeyId} matchedRegistrationCount={MatchedRegistrationCount}",
                    trimmedApiKeyId, active.Length);
            return null;
        }
        return string.IsNullOrWhiteSpace(active[0].ScopeId) ? null : active[0].ScopeId.Trim();

    }
}
