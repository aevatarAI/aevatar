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
}
