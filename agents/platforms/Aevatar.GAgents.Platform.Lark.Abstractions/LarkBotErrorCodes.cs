namespace Aevatar.GAgents.Platform.Lark.Abstractions;

/// <summary>
/// Documented Lark Open Platform error codes that the runtime branches on. Add new entries only
/// when behavior depends on the specific code; generic error surfacing should keep using the
/// textual <c>msg</c> from the response body.
/// </summary>
public static class LarkBotErrorCodes
{
    /// <summary>
    /// "The operator has no permission to react on the specific message".
    /// </summary>
    public const int NoPermissionToReact = 231002;

    /// <summary>
    /// "open_id cross app" - Lark <c>open_id</c> is app-scoped.
    /// </summary>
    public const int OpenIdCrossApp = 99992361;

    /// <summary>
    /// "user id cross tenant" - Lark <c>union_id</c> is tenant-scoped.
    /// </summary>
    public const int UserIdCrossTenant = 99992364;

    /// <summary>
    /// "Bot is not in the chat".
    /// </summary>
    public const int BotNotInChat = 230002;
}
