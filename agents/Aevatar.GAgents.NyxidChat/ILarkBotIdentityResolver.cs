namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Resolves the bot's own Lark <c>open_id</c> so the group-chat admission gate can tell whether an
/// inbound @-mention addressed the bot. NyxID never forwards the bot's own identity in the relay
/// payload, so it is resolved on demand from Lark's <c>bot/v3/info</c> endpoint through the same
/// proxy used to send replies and reactions. Returns <c>null</c> when the identity cannot be
/// determined (missing provider/token, transport error, or an unexpected response) so the gate can
/// fail open and engage rather than going silent.
/// </summary>
public interface ILarkBotIdentityResolver
{
    Task<string?> ResolveBotOpenIdAsync(string providerSlug, string accessToken, CancellationToken ct);
}
