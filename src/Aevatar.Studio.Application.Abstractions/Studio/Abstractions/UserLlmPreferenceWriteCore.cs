namespace Aevatar.Studio.Application.Studio.Abstractions;

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
        var keyMatches = options
            .Where(option => IsSameOption(option, normalized))
            .ToArray();
        var relatedMatches = directMatches.Length == 0
            ? Array.Empty<UserLlmOption>()
            : options
                .Where(option => directMatches.Any(direct => ShareOptionKey(option, direct)))
                .ToArray();
        var matches = directMatches
            .Concat(keyMatches)
            .Concat(relatedMatches)
            .Distinct()
            .ToArray();

        return ChoosePreferredOption(matches.Where(IsSelectable)) ??
               ChoosePreferredOption(matches);
    }

    public static UserLlmOption? ChoosePreferredOption(IEnumerable<UserLlmOption> options)
    {
        var ranked = options
            .Select(option => new
            {
                Option = option,
                SelectabilityRank = OptionSelectabilityRank(option),
                SourceRank = OptionSourceRank(option),
            })
            .OrderByDescending(candidate => candidate.SelectabilityRank)
            .ThenByDescending(candidate => candidate.SourceRank)
            .Take(2)
            .ToArray();

        return ranked.Length switch
        {
            0 => null,
            1 => ranked[0].Option,
            _ when ranked[0].SelectabilityRank != ranked[1].SelectabilityRank ||
                   ranked[0].SourceRank != ranked[1].SourceRank => ranked[0].Option,
            _ => null,
        };
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

    private static bool ShareOptionKey(UserLlmOption left, UserLlmOption right) =>
        EqualIfPresent(left.ServiceId, right.ServiceId) ||
        EqualIfPresent(left.ServiceSlug, right.ServiceSlug) ||
        EqualIfPresent(left.RouteValue, right.RouteValue);

    private static bool EqualIfPresent(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static int OptionSelectabilityRank(UserLlmOption option)
    {
        var ready = string.Equals(option.Status, UserLlmRouteStatus.Ready, StringComparison.OrdinalIgnoreCase);
        return (option.Allowed, ready) switch
        {
            (true, true) => 3,
            (true, false) => 2,
            (false, true) => 1,
            _ => 0,
        };
    }

    private static int OptionSourceRank(UserLlmOption option)
    {
        var normalized = UserLlmCatalogNormalization.NormalizeSource(option.Source).ToWireValue();
        return normalized switch
        {
            UserLlmRouteSource.UserService => 3,
            UserLlmRouteSource.ProxyService => 2,
            UserLlmRouteSource.GatewayProvider => 1,
            _ => 0,
        };
    }

    public static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
