namespace Aevatar.Studio.Application.Studio.Abstractions;

public static class UserLlmSelectionRoute
{
    public static string? Resolve(UserLlmSelectionValue? selection) => selection?.Kind switch
    {
        UserLlmSelectionKind.Gateway => UserConfigLlmRouteDefaults.Gateway,
        UserLlmSelectionKind.NyxIdUserService => NormalizeOptional(selection.RouteValue),
        _ => null,
    };

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
