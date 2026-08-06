using Aevatar.AI.Abstractions;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public static class UserLlmSelectionRoute
{
    public static string? Resolve(LLMSelection? selection) => selection?.RouteKind switch
    {
        LLMRouteKind.Gateway => UserConfigLlmRouteDefaults.Gateway,
        LLMRouteKind.NyxIdUserService => NormalizeOptional(selection.RouteValue),
        _ => null,
    };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
