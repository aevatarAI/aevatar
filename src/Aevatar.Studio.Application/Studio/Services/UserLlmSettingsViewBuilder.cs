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
        string savedRoute,
        string defaultModel)
    {
        var options = BuildRouteOptions(result.Services);
        var readyRoutes = options
            .Where(option => option.Ready && option.Allowed)
            .Select(option => option.RouteValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var effectiveRoute = ResolveEffectiveRoute(savedRoute, options);
        var effectiveRouteLabel = ResolveRouteLabel(effectiveRoute, options);
        var savedRouteLabel = ResolveRouteLabel(savedRoute, options);
        var catalogStatus = result.Services.Count == 0
            ? UserLlmCatalogStatusValue.Empty
            : UserLlmCatalogStatusValue.Ready;
        var routeFallbackActive = !string.Equals(savedRoute, effectiveRoute, StringComparison.OrdinalIgnoreCase);
        var fallbackReason = routeFallbackActive
            ? UserLlmFallbackReasonValue.SavedRouteUnavailable.ToWireValue()
            : null;
        var modelGroups = BuildModelGroups(result.Services, options, savedRoute, effectiveRoute);
        var canSave = readyRoutes.Contains(UserConfigLlmRouteDefaults.Gateway) || readyRoutes.Count > 0;

        return new UserLlmSettingsView(
            SavedRoute: savedRoute,
            SavedRouteLabel: savedRouteLabel,
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
        string savedRoute,
        string defaultModel)
    {
        var label = string.Equals(savedRoute, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase)
            ? _gatewayRouteLabel
            : savedRoute;

        return new UserLlmSettingsView(
            SavedRoute: savedRoute,
            SavedRouteLabel: label,
            EffectiveRoute: savedRoute,
            EffectiveRouteLabel: label,
            RouteFallbackActive: false,
            FallbackReason: UserLlmFallbackReasonValue.CatalogUnavailable.ToWireValue(),
            RouteOptions:
            [
                new UserLlmRouteOption(
                    RouteValue: savedRoute,
                    Label: label,
                    Source: string.Equals(savedRoute, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase)
                        ? UserLlmRouteSourceValue.GatewayProvider.ToWireValue()
                        : UserLlmRouteSourceValue.UserService.ToWireValue(),
                    Status: UserLlmRouteStatusValue.Unavailable.ToWireValue(),
                    Allowed: false,
                    Ready: false,
                    ServiceId: null,
                    ServiceSlug: null,
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            UserConfigLlmRouteDefaults.Gateway,
        };

        foreach (var service in services)
        {
            if (!IsUserServiceRoute(service))
                continue;

            var route = UserConfigLlmRoute.Normalize(service.RouteValue);
            if (!seen.Add(route))
                continue;

            options.Add(new UserLlmRouteOption(
                RouteValue: route,
                Label: NormalizeDisplayName(service.DisplayName, service.ServiceSlug),
                Source: UserLlmCatalogNormalization.NormalizeSource(service.Source).ToWireValue(),
                Status: UserLlmCatalogNormalization.NormalizeStatus(service.Status).ToWireValue(),
                Allowed: service.Allowed,
                Ready: UserLlmCatalogNormalization.IsReady(service),
                ServiceId: service.UserServiceId,
                ServiceSlug: service.ServiceSlug,
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
            ServiceId: null,
            ServiceSlug: null,
            Description: null);
    }

    private static string ResolveEffectiveRoute(
        string savedRoute,
        IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (routeOptions.Any(option =>
                option.Ready &&
                option.Allowed &&
                string.Equals(option.RouteValue, savedRoute, StringComparison.OrdinalIgnoreCase)))
        {
            return savedRoute;
        }

        var gateway = routeOptions.FirstOrDefault(option =>
            option.Ready &&
            option.Allowed &&
            string.Equals(option.RouteValue, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase));
        if (gateway is not null)
            return gateway.RouteValue;

        return routeOptions.FirstOrDefault(option => option.Ready && option.Allowed)?.RouteValue ?? savedRoute;
    }

    private static IReadOnlyList<UserLlmModelGroup> BuildModelGroups(
        IReadOnlyList<NyxIdLlmService> services,
        IReadOnlyList<UserLlmRouteOption> routeOptions,
        string savedRoute,
        string effectiveRoute)
    {
        var groups = new List<UserLlmModelGroup>();
        var routesToInclude = routeOptions
            .Select(option => option.RouteValue)
            .Append(savedRoute)
            .Append(effectiveRoute)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var route in routesToInclude)
        {
            var routeServices = string.Equals(route, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase)
                ? services.Where(IsGatewayRouteService)
                : services.Where(service =>
                    IsUserServiceRoute(service) &&
                    string.Equals(UserConfigLlmRoute.Normalize(service.RouteValue), route, StringComparison.OrdinalIgnoreCase));
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

                var groupId = string.IsNullOrWhiteSpace(service.ServiceSlug)
                    ? service.UserServiceId
                    : service.ServiceSlug;
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

    private string ResolveRouteLabel(string route, IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (string.Equals(route, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase))
            return _gatewayRouteLabel;

        return routeOptions.FirstOrDefault(option =>
            string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase))?.Label ?? route;
    }

    private static string NormalizeDisplayName(string? displayName, string fallback)
    {
        var normalized = UserLlmPreferenceWriteCore.NormalizeOptional(displayName);
        return normalized ?? fallback.Trim();
    }
}
