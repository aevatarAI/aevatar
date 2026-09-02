using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal static class StudioToolScopeResolver
{
    public static string? ResolveOwnerScopeOrCallerScope() =>
        Normalize(AgentToolRequestContext.OwnerScopeId) ?? Normalize(AgentToolRequestContext.ScopeId);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
