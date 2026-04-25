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
}
