namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Resolves the canonical Aevatar scope id for a Nyx relay callback when it cannot
/// be derived from the callback JWT itself.
/// </summary>
/// <remarks>
/// NyxID's relay callback JWT (see ChronoAIProject/NyxID#504) only carries
/// channel-routing claims (api_key_id, message_id, platform, body_sha256, jti) -
/// it does not emit any scope / sub / nameid claim. The relay endpoint still needs a
/// scope id to address the per-tenant ConversationGAgent actor, so it falls back
/// from the validator's claim-based extraction to this resolver, which looks up the
/// scope id recorded against the bot registration during provisioning.
///
/// This port lives in NyxidChat to keep the relay endpoint independent from the
/// ChannelRuntime implementation project. The implementation in ChannelRuntime
/// delegates to the channel bot registration query port.
/// </remarks>
public interface INyxIdRelayScopeResolver
{
    Task<string?> ResolveScopeIdByApiKeyAsync(string nyxAgentApiKeyId, CancellationToken ct = default);
}
