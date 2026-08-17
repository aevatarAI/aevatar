using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Abstractions.LLMProviders;

public sealed record LLMControlContext(
    string? NyxIdAccessToken,
    string? NyxIdOrgToken,
    string? SenderNyxIdAccessToken,
    string? ModelOverride,
    string? NyxIdRoutePreference,
    int? MaxToolRoundsOverride,
    string? UserMemoryPrompt)
{
    public static LLMControlContext Empty { get; } = new(null, null, null, null, null, null, null);

    public LLMRouteTarget? RouteTarget { get; init; }

    public AgentToolExecutionContext ToToolContext(AgentToolExecutionContext? baseContext = null)
    {
        var context = baseContext ?? AgentToolExecutionContext.Empty;
        var toolContextOwnsNyxIdCredential =
            context.Credentials.NyxIdCredentialAuthority ==
            AgentToolNyxIdCredentialAuthority.ToolExecutionContext;
        return context with
        {
            Credentials = context.Credentials with
            {
                NyxIdAccessToken = toolContextOwnsNyxIdCredential
                    ? Normalize(context.Credentials.NyxIdAccessToken) ?? Normalize(NyxIdAccessToken)
                    : Normalize(NyxIdAccessToken) ?? context.Credentials.NyxIdAccessToken,
                NyxIdOrgToken = toolContextOwnsNyxIdCredential
                    ? Normalize(context.Credentials.NyxIdOrgToken) ?? Normalize(NyxIdOrgToken)
                    : Normalize(NyxIdOrgToken) ?? context.Credentials.NyxIdOrgToken,
                SenderNyxIdAccessToken = Normalize(SenderNyxIdAccessToken) ?? context.Credentials.SenderNyxIdAccessToken,
            },
            Routing = context.Routing with
            {
                ModelOverride = Normalize(ModelOverride) ?? context.Routing.ModelOverride,
                NyxIdRoutePreference = Normalize(NyxIdRoutePreference) ?? context.Routing.NyxIdRoutePreference,
                RouteTarget = RouteTarget?.Clone() ?? context.Routing.RouteTarget?.Clone(),
                MaxToolRoundsOverride = MaxToolRoundsOverride ?? context.Routing.MaxToolRoundsOverride,
                UserMemoryPrompt = Normalize(UserMemoryPrompt) ?? context.Routing.UserMemoryPrompt,
            },
        };
    }

    public LLMRequestRoutingContext ToRoutingContext(LLMRequestRoutingContext? baseRouting = null)
    {
        var routing = baseRouting ?? LLMRequestRoutingContext.Empty;
        return routing with
        {
            ModelOverride = Normalize(ModelOverride) ?? routing.ModelOverride,
            NyxIdRoutePreference = Normalize(NyxIdRoutePreference) ?? routing.NyxIdRoutePreference,
            RouteTarget = RouteTarget?.Clone() ?? routing.RouteTarget?.Clone(),
            MaxToolRoundsOverride = MaxToolRoundsOverride ?? routing.MaxToolRoundsOverride,
            UserMemoryPrompt = Normalize(UserMemoryPrompt) ?? routing.UserMemoryPrompt,
        };
    }

    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
