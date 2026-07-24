using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Application.Studio.Services;

internal sealed class UserLlmSettingsViewBuilder
{
    private readonly string _gatewayRouteLabel;

    public UserLlmSettingsViewBuilder(string gatewayRouteLabel)
    {
        _gatewayRouteLabel = UserLlmPreferenceWriteCore.NormalizeOptional(gatewayRouteLabel) ?? "NyxID Gateway";
    }

    public UserLlmSettingsView BuildAvailable(
        NyxIdLlmServicesResult result,
        UserLlmSelectionValue? savedSelection,
        string defaultModel)
    {
        var saved = ResolveSavedSelection(savedSelection);
        var options = BuildRouteOptions(result.Services);
        var readyRoutes = options
            .Where(option => option.Ready && option.Allowed)
            .Select(option => option.RouteValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectiveRoute = ResolveEffectiveRoute(saved, options);
        var effectiveRouteLabel = ResolveEffectiveRouteLabel(saved, effectiveRoute, options);
        var savedRouteLabel = ResolveSavedRouteLabel(saved, options);
        var catalogStatus = result.Services.Count == 0
            ? UserLlmCatalogStatusValue.Empty
            : UserLlmCatalogStatusValue.Ready;
        var routeFallbackActive = IsFallbackActive(saved, effectiveRoute, options);
        var fallbackReason = routeFallbackActive
            ? UserLlmFallbackReasonValue.SavedRouteUnavailable.ToWireValue()
            : null;
        var modelGroups = BuildModelGroups(result.Services, options, saved.Route, effectiveRoute);
        var canSave = readyRoutes.Contains(UserConfigLlmRouteDefaults.Gateway) || readyRoutes.Count > 0;

        return new UserLlmSettingsView(
            SavedRoute: saved.Route,
            SavedRouteLabel: savedRouteLabel,
            SavedRouteKind: UserLlmSelectionKindWire.From(saved.Kind),
            SavedUserServiceId: saved.UserServiceId,
            SavedServiceSlug: saved.ServiceSlug,
            EffectiveRoute: effectiveRoute,
            EffectiveRouteLabel: effectiveRouteLabel,
            RouteFallbackActive: routeFallbackActive,
            FallbackReason: fallbackReason,
            RouteOptions: options,
            ModelGroupsByRoute: modelGroups,
            CatalogStatus: catalogStatus.ToWireValue(),
            Capabilities: new UserLlmSettingsCapabilities(
                CanEditRoute: catalogStatus == UserLlmCatalogStatusValue.Ready && options.Count > 0,
                CanEditModel: catalogStatus == UserLlmCatalogStatusValue.Ready && readyRoutes.Count > 0,
                CanSave: canSave,
                CanRetryCatalog: false),
            DefaultModel: defaultModel,
            SetupHint: result.SetupHint);
    }

    public UserLlmSettingsView BuildUnavailable(
        UserLlmSelectionValue? savedSelection,
        string defaultModel)
    {
        var saved = ResolveSavedSelection(savedSelection);
        var savedRouteLabel = saved.Kind switch
        {
            UserLlmSelectionKind.Gateway => _gatewayRouteLabel,
            UserLlmSelectionKind.Unspecified => string.Empty,
            _ => saved.ServiceSlug ?? saved.Route,
        };
        var effectiveRoute = saved.Kind == UserLlmSelectionKind.Unspecified
            ? UserConfigLlmRouteDefaults.Gateway
            : saved.Route;
        var effectiveRouteLabel = saved.Kind == UserLlmSelectionKind.Unspecified
            ? _gatewayRouteLabel
            : savedRouteLabel;

        return new UserLlmSettingsView(
            SavedRoute: saved.Route,
            SavedRouteLabel: savedRouteLabel,
            SavedRouteKind: UserLlmSelectionKindWire.From(saved.Kind),
            SavedUserServiceId: saved.UserServiceId,
            SavedServiceSlug: saved.ServiceSlug,
            EffectiveRoute: effectiveRoute,
            EffectiveRouteLabel: effectiveRouteLabel,
            RouteFallbackActive: false,
            FallbackReason: UserLlmFallbackReasonValue.CatalogUnavailable.ToWireValue(),
            RouteOptions:
            [
                new UserLlmRouteOption(
                    RouteValue: effectiveRoute,
                    Label: effectiveRouteLabel,
                    Source: saved.Kind == UserLlmSelectionKind.NyxIdUserService
                        ? UserLlmRouteSourceValue.UserService.ToWireValue()
                        : UserLlmRouteSourceValue.GatewayProvider.ToWireValue(),
                    Status: UserLlmRouteStatusValue.Unavailable.ToWireValue(),
                    Allowed: false,
                    Ready: false,
                    UserServiceId: saved.UserServiceId,
                    ServiceSlug: saved.ServiceSlug,
                    DefaultModel: null,
                    Description: null),
            ],
            ModelGroupsByRoute: [],
            CatalogStatus: UserLlmCatalogStatusValue.Unavailable.ToWireValue(),
            Capabilities: new UserLlmSettingsCapabilities(
                CanEditRoute: false,
                CanEditModel: false,
                CanSave: false,
                CanRetryCatalog: true),
            DefaultModel: defaultModel,
            SetupHint: null);
    }

    private IReadOnlyList<UserLlmRouteOption> BuildRouteOptions(
        IReadOnlyList<NyxIdLlmService> services)
    {
        var options = new List<UserLlmRouteOption>
        {
            BuildGatewayRouteOption(services),
        };
        var seenInventoryIds = new HashSet<string>(StringComparer.Ordinal);
        var seenDiagnostics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in services)
        {
            if (!IsUserServiceRoute(service))
                continue;

            var route = UserLlmPreferenceWriteCore.NormalizeOptional(service.RouteValue) ?? string.Empty;
            var userServiceId = InventoryUserServiceId(service);
            if (userServiceId is not null)
            {
                if (!seenInventoryIds.Add(userServiceId))
                    continue;
            }
            else
            {
                var diagnosticKey = UserLlmPreferenceWriteCore.NormalizeOptional(service.CatalogEntryId) is { } entryId
                    ? $"id:{entryId}"
                    : $"route:{route}";
                if (!seenDiagnostics.Add(diagnosticKey))
                    continue;
            }

            options.Add(new UserLlmRouteOption(
                RouteValue: route,
                Label: NormalizeDisplayName(service.DisplayName, service.ServiceSlug),
                Source: UserLlmCatalogNormalization.NormalizeSource(service.Source).ToWireValue(),
                Status: UserLlmCatalogNormalization.NormalizeStatus(service.Status).ToWireValue(),
                Allowed: service.Allowed,
                Ready: UserLlmCatalogNormalization.IsReady(service),
                UserServiceId: userServiceId,
                ServiceSlug: service.ServiceSlug,
                DefaultModel: userServiceId is null
                    ? null
                    : UserLlmPreferenceWriteCore.NormalizeOptional(service.DefaultModel),
                Description: UserLlmPreferenceWriteCore.NormalizeOptional(service.Description)));
        }

        return options;
    }

    private UserLlmRouteOption BuildGatewayRouteOption(IReadOnlyList<NyxIdLlmService> services)
    {
        var gatewayServices = services.Where(IsGatewayRouteService).ToArray();
        var hasAny = gatewayServices.Length > 0;
        var ready = gatewayServices.Any(UserLlmCatalogNormalization.IsReady);
        var allowed = !hasAny || gatewayServices.Any(service => service.Allowed);
        var status = !hasAny
            ? UserLlmRouteStatusValue.Ready
            : ready
                ? UserLlmRouteStatusValue.Ready
                : UserLlmCatalogNormalization.NormalizeStatus(gatewayServices[0].Status);

        return new UserLlmRouteOption(
            RouteValue: UserConfigLlmRouteDefaults.Gateway,
            Label: _gatewayRouteLabel,
            Source: UserLlmRouteSourceValue.GatewayProvider.ToWireValue(),
            Status: status.ToWireValue(),
            Allowed: allowed,
            Ready: !hasAny || ready,
            UserServiceId: null,
            ServiceSlug: null,
            DefaultModel: null,
            Description: null);
    }

    private static string ResolveEffectiveRoute(
        SavedSelection saved,
        IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (FindSavedOption(saved, routeOptions) is { Ready: true, Allowed: true } selected)
            return selected.RouteValue;

        var gateway = routeOptions.FirstOrDefault(option =>
            option.Ready &&
            option.Allowed &&
            string.Equals(option.RouteValue, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase));
        if (gateway is not null)
            return gateway.RouteValue;

        return routeOptions.FirstOrDefault(option => option.Ready && option.Allowed)?.RouteValue ?? saved.Route;
    }

    private static bool IsFallbackActive(
        SavedSelection saved,
        string effectiveRoute,
        IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (saved.Kind == UserLlmSelectionKind.Unspecified)
            return false;

        var savedOption = FindSavedOption(saved, routeOptions);
        if (saved.Kind == UserLlmSelectionKind.NyxIdUserService)
            return savedOption is not { Ready: true, Allowed: true };

        return !string.Equals(saved.Route, effectiveRoute, StringComparison.OrdinalIgnoreCase);
    }

    private static UserLlmRouteOption? FindSavedOption(
        SavedSelection saved,
        IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (saved.Kind == UserLlmSelectionKind.NyxIdUserService)
        {
            if (saved.UserServiceId is null)
                return null;

            return routeOptions.FirstOrDefault(option =>
                string.Equals(option.UserServiceId, saved.UserServiceId, StringComparison.Ordinal));
        }

        return routeOptions.FirstOrDefault(option =>
            string.Equals(option.RouteValue, saved.Route, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<UserLlmModelGroup> BuildModelGroups(
        IReadOnlyList<NyxIdLlmService> services,
        IReadOnlyList<UserLlmRouteOption> routeOptions,
        string selectedRoute,
        string effectiveRoute)
    {
        var groups = new List<UserLlmModelGroup>();
        var routesToInclude = routeOptions
            .Select(option => option.RouteValue)
            .Append(selectedRoute)
            .Append(effectiveRoute)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var route in routesToInclude)
        {
            var routeServices = string.Equals(route, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase)
                ? services.Where(IsGatewayRouteService)
                : services.Where(service =>
                    IsUserServiceRoute(service) &&
                    string.Equals(
                        UserLlmPreferenceWriteCore.NormalizeOptional(service.RouteValue),
                        route,
                        StringComparison.OrdinalIgnoreCase));
            foreach (var service in routeServices)
            {
                var models = service.Models
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Select(model => model.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (models.Length == 0)
                    continue;

                var groupId = InventoryUserServiceId(service) ??
                              UserLlmPreferenceWriteCore.NormalizeOptional(service.CatalogEntryId) ??
                              service.ServiceSlug;
                groups.Add(new UserLlmModelGroup(
                    RouteValue: route,
                    GroupId: groupId,
                    Label: NormalizeDisplayName(service.DisplayName, service.ServiceSlug),
                    Models: models));
            }
        }

        return groups
            .GroupBy(group => $"{group.RouteValue}\u001f{group.GroupId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsGatewayRouteService(NyxIdLlmService service) =>
        !IsUserServiceRoute(service);

    private static bool IsUserServiceRoute(NyxIdLlmService service) =>
        UserLlmCatalogNormalization.NormalizeSource(service.Source).IsUserServiceRoute;

    private static string? InventoryUserServiceId(NyxIdLlmService service) =>
        service.Identity is
        {
            Authority: UserLlmIdentityAuthority.NyxIdUserServicesInventory,
        } identity
            ? UserLlmPreferenceWriteCore.NormalizeOptional(identity.NyxIdUserServiceId)
            : null;

    private string ResolveSavedRouteLabel(
        SavedSelection saved,
        IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (saved.Kind == UserLlmSelectionKind.Gateway)
            return _gatewayRouteLabel;

        return FindSavedOption(saved, routeOptions)?.Label ?? saved.ServiceSlug ?? saved.Route;
    }

    private string ResolveEffectiveRouteLabel(
        SavedSelection saved,
        string effectiveRoute,
        IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (FindSavedOption(saved, routeOptions) is { Ready: true, Allowed: true } savedOption &&
            string.Equals(savedOption.RouteValue, effectiveRoute, StringComparison.OrdinalIgnoreCase))
        {
            return savedOption.Label;
        }

        return ResolveRouteLabel(effectiveRoute, routeOptions);
    }

    private string ResolveRouteLabel(string route, IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (string.Equals(route, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase))
            return _gatewayRouteLabel;

        return routeOptions.FirstOrDefault(option =>
            string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase))?.Label ?? route;
    }

    private static SavedSelection ResolveSavedSelection(UserLlmSelectionValue? selection)
    {
        if (selection is null or { Kind: UserLlmSelectionKind.Unspecified })
        {
            return new SavedSelection(
                UserLlmSelectionKind.Unspecified,
                string.Empty,
                null,
                null);
        }

        if (selection is { Kind: UserLlmSelectionKind.Gateway })
        {
            return new SavedSelection(
                UserLlmSelectionKind.Gateway,
                UserConfigLlmRouteDefaults.Gateway,
                null,
                null);
        }

        if (selection is { Kind: UserLlmSelectionKind.NyxIdUserService })
        {
            return new SavedSelection(
                UserLlmSelectionKind.NyxIdUserService,
                UserLlmSelectionRoute.Resolve(selection) ?? string.Empty,
                UserLlmPreferenceWriteCore.NormalizeOptional(selection.NyxIdUserServiceId),
                UserLlmPreferenceWriteCore.NormalizeOptional(selection.ServiceSlugSnapshot));
        }

        return new SavedSelection(
            UserLlmSelectionKind.Unspecified,
            string.Empty,
            null,
            null);
    }

    private static string NormalizeDisplayName(string? displayName, string fallback)
    {
        var normalized = UserLlmPreferenceWriteCore.NormalizeOptional(displayName);
        return normalized ?? fallback.Trim();
    }

    private sealed record SavedSelection(
        UserLlmSelectionKind Kind,
        string Route,
        string? UserServiceId,
        string? ServiceSlug);
}
