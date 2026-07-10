namespace Aevatar.AI.ToolProviders.ToolSetRegistry;

public static class ToolSetNames
{
    public const string WorkspaceDefault = "workspace.default";
    public const string LarkSelfNotify = "lark.self_notify";

    /// <summary>
    /// Opt-in tool set that exposes the caller's <c>x-aevatar-tool</c>-marked NyxID
    /// connected-service operations as individual tools. Kept out of
    /// <see cref="WorkspaceDefault"/> so connected services are only injected when a route
    /// policy explicitly selects this set.
    /// </summary>
    public const string NyxIdConnectedServices = "nyxid.connected_services";
}
