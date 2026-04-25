namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Documented Lark Open Platform error codes that the runtime branches on. Add new entries only
/// when behavior depends on the specific code (e.g. log gating, retry decisions); generic error
/// surfacing should keep using the textual <c>msg</c> from the response body.
/// </summary>
internal static class LarkBotErrorCodes
{
    /// <summary>
    /// "The operator has no permission to react on the specific message" — recurring tenant
    /// config gap when the bot's app scope is missing the reaction permission.
    /// </summary>
    public const int NoPermissionToReact = 231002;

    /// <summary>
    /// "open_id cross app" — Lark <c>open_id</c> is app-scoped (each Lark app issues its own
    /// <c>ou_*</c> for the same user). When relay-side ingress (e.g. NyxID's Lark app) and
    /// outbound (e.g. customer's <c>api-lark-bot</c>) are different apps, sending to a
    /// <c>receive_id_type=open_id</c> with the relay-app-scoped <c>ou_*</c> is rejected. Surfaces
    /// on legacy SkillRunner / human-interaction state captured before <c>union_id</c> ingress
    /// existed; rebuild the agent (e.g. <c>/agents</c> → Delete → <c>/daily</c>) to pin the new
    /// cross-app safe pair.
    /// </summary>
    public const int OpenIdCrossApp = 99992361;

    /// <summary>
    /// "user id cross tenant" — Lark <c>union_id</c> is tenant-scoped. When the relay-side
    /// ingress Lark app and the outbound proxy Lark app live in different Lark tenants (e.g.
    /// NyxID-administered <c>api-lark-bot</c> proxy bound to a different tenant than the user's
    /// own bot that subscribed to events), <c>receive_id_type=union_id</c> is rejected.
    /// Resolution is configuration-side: align the NyxID proxy's downstream Lark app with the
    /// channel-bot that received the inbound event, OR rebuild the agent so the new
    /// <c>chat_id</c>-preferred path takes effect (chat_id traverses no user-id translation).
    /// </summary>
    public const int UserIdCrossTenant = 99992364;

    /// <summary>
    /// "Bot is not in the chat" — the outbound app is not a member of the chat referenced by
    /// <c>receive_id_type=chat_id</c>. For DMs, each Lark app has its own DM thread with the
    /// user, so a chat_id captured by the relay-side ingress app is rejected by a different
    /// outbound app even within the same tenant. Triggers the runtime fallback to the
    /// secondary delivery target (typically union_id) in
    /// <c>SkillRunnerGAgent.SendOutputAsync</c> and
    /// <c>FeishuCardHumanInteractionPort.SendMessageAsync</c>.
    /// </summary>
    public const int BotNotInChat = 230002;
}
