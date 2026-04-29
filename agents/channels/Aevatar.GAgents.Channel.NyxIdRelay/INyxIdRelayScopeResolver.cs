namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Resolves the Aevatar scope id associated with a Nyx relay API key for
/// non-admission enrichment paths.
/// </summary>
/// <remarks>
/// The relay webhook admission path must not use this projection-backed lookup
/// to choose a tenant. Callback routing requires a verified scope from the
/// callback token or another authoritative admission contract. This resolver is
/// kept only for optional enrichment such as loading the bot owner's display or
/// LLM preferences, where projection lag must fail closed to "no enrichment".
///
/// This port lives in NyxidChat to keep the relay endpoint independent from the
/// ChannelRuntime implementation project. The implementation in ChannelRuntime
/// delegates to the channel bot registration query port.
/// </remarks>
public interface INyxIdRelayScopeResolver
{
    Task<string?> ResolveScopeIdByApiKeyAsync(string nyxAgentApiKeyId, CancellationToken ct = default);
}
