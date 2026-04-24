using Aevatar.GAgents.NyxidChat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgents.ChannelRuntime;

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

        // Distinct non-empty scope ids across all matching registrations. If repeated
        // mirror / provision flows persisted multiple entries with the same api key id
        // but different scope ids, the relay turn cannot be safely routed to a single
        // tenant — refuse rather than risk dispatching to the wrong ConversationGAgent.
        var distinctScopeIds = entries
            .Select(entry => entry.ScopeId?.Trim())
            .Where(scopeId => !string.IsNullOrWhiteSpace(scopeId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinctScopeIds.Length == 0)
            return null;

        if (distinctScopeIds.Length > 1)
        {
            _logger.LogWarning(
                "Refusing relay scope resolution for ambiguous Nyx agent api key id: apiKeyId={ApiKeyId} matchedScopeCount={MatchedScopeCount}",
                trimmedApiKeyId,
                distinctScopeIds.Length);
            return null;
        }

        return distinctScopeIds[0];
    }
}
