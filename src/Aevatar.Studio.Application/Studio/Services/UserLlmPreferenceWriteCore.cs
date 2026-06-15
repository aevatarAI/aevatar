using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

public static class UserLlmPreferenceWriteCore
{
    public static UserConfig Reset(UserConfig current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            DefaultModel = string.Empty,
            PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
        };
    }

    public static UserConfig MergeGatewayRoute(UserConfig current, string? model)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
            DefaultModel = NormalizeOptional(model) ?? current.DefaultModel,
        };
    }

    public static UserConfig MergeSelectedOption(
        UserConfig current,
        UserLlmOption option,
        string? model,
        bool preserveCurrentModelWhenMissing)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(option);
        EnsureSelectable(option);

        var resolvedDefaultModel = NormalizeModelForRoute(NormalizeOptional(model), option) ??
                                   option.DefaultModel ??
                                   (preserveCurrentModelWhenMissing ? current.DefaultModel : string.Empty);
        return current with
        {
            PreferredLlmRoute = UserConfigLlmRoute.Normalize(option.RouteValue),
            DefaultModel = resolvedDefaultModel,
        };
    }

    public static UserConfig MergeRawModel(UserConfig current, string model)
    {
        ArgumentNullException.ThrowIfNull(current);
        var normalized = model.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? current with { DefaultModel = string.Empty }
            : current with { DefaultModel = normalized };
    }

    public static UserLlmOption? FindOption(IReadOnlyList<UserLlmOption> options, string requested)
    {
        var normalized = requested.Trim();
        var directMatches = options
            .Where(option => string.Equals(option.ServiceId, normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var directSelectable = directMatches.Where(IsSelectable).Take(2).ToArray();
        if (directSelectable.Length == 1)
            return directSelectable[0];

        var keyMatches = options
            .Where(option => IsSameOption(option, normalized))
            .ToArray();
        var selectable = keyMatches.Where(IsSelectable).Take(2).ToArray();
        if (selectable.Length == 1)
            return selectable[0];

        return directMatches.FirstOrDefault() ?? (keyMatches.Length == 1 ? keyMatches[0] : null);
    }

    public static bool IsSelectable(UserLlmOption option) =>
        option.Allowed && string.Equals(option.Status, UserLlmRouteStatus.Ready, StringComparison.OrdinalIgnoreCase);

    public static void EnsureSelectable(UserLlmOption option)
    {
        if (!option.Allowed)
            throw new InvalidOperationException($"LLM service '{option.DisplayName}' is not allowed for this user.");

        if (!string.Equals(option.Status, UserLlmRouteStatus.Ready, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"LLM service '{option.DisplayName}' is not ready: {option.Status}.");
    }

    public static string? NormalizeModelForRoute(string? model, UserLlmOption option)
    {
        if (model is null)
            return null;

        if (UserConfigLlmModel.TryParseRouteModel(model) is { } prefixed &&
            IsSameOption(option, prefixed.RouteSlug))
        {
            return prefixed.Model;
        }

        return model;
    }

    public static bool IsSameOption(UserLlmOption option, string requested) =>
        string.Equals(option.ServiceId, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.ServiceSlug, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.DisplayName, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.RouteValue, UserConfigLlmRoute.Normalize(requested), StringComparison.OrdinalIgnoreCase);

    public static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
