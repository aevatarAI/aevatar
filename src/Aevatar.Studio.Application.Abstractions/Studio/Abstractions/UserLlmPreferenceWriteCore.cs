namespace Aevatar.Studio.Application.Studio.Abstractions;

public static class UserLlmPreferenceWriteCore
{
    public static bool IsGatewayWriteAlias(string? routeValue)
    {
        if (routeValue is null)
            return false;

        var normalized = routeValue.Trim();
        return normalized.Length == 0 ||
               string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "gateway", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, UserConfigLlmRouteDefaults.Gateway, StringComparison.Ordinal);
    }

    public static UserLlmOption RequireInventoryOption(
        IReadOnlyList<UserLlmOption> options,
        string userServiceId)
    {
        ArgumentNullException.ThrowIfNull(options);
        var id = userServiceId.Trim();
        var matches = options.Where(option =>
            option.Identity is
            {
                Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
            } identity &&
            string.Equals(identity.NyxIdUserServiceId, id, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException($"LLM user service '{id}' is not selectable.");
    }

    public static UserLlmSelectionValue BuildGatewaySelection() => new(
        UserLlmSelectionKind.Gateway,
        UserConfigLlmRouteDefaults.Gateway,
        string.Empty,
        string.Empty);

    public static UserLlmSelectionValue BuildInventorySelection(UserLlmOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        EnsureSelectable(option);
        var identity = option.Identity;
        if (identity is not { Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory } ||
            string.IsNullOrWhiteSpace(identity.NyxIdUserServiceId))
        {
            throw new InvalidOperationException("LLM selection requires a NyxID inventory identity.");
        }

        var slug = NormalizeOptional(option.ServiceSlug) ??
                   throw new InvalidOperationException("LLM selection requires a service slug.");
        return new UserLlmSelectionValue(
            UserLlmSelectionKind.NyxIdUserService,
            $"/api/v1/proxy/s/{slug}",
            identity.NyxIdUserServiceId.Trim(),
            slug);
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
        var normalized = NormalizeOptional(model);
        if (normalized is null)
            return null;

        if (UserConfigLlmModel.TryParseRouteModel(normalized) is not { } prefixed)
            return normalized;

        if (!string.Equals(option.ServiceSlug, prefixed.RouteSlug, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"LLM model route '{prefixed.RouteSlug}' does not match the selected user service.");
        }

        return prefixed.Model;
    }

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
