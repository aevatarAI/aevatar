using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Binding.Tools;

internal static class ToolOwnerScopeResolver
{
    public static string? Resolve() =>
        Normalize(AgentToolRequestContext.OwnerScopeId);

    public static string MissingMessage => "owner_scope_id not available in request context";

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
