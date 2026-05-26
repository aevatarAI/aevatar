using Aevatar.Studio.Application.Studio.Abstractions;
using Microsoft.Extensions.Options;

namespace Aevatar.Studio.Application.Studio.Services;

public sealed class UserLlmPreferenceService : IUserLlmPreferenceService
{
    private const string DefaultGatewayRouteLabel = "NyxID Gateway";

    private readonly IUserConfigQueryPort _queryPort;
    private readonly IUserLlmCatalogPort _catalogPort;
    private readonly string _gatewayRouteLabel;

    public UserLlmPreferenceService(
        IUserConfigQueryPort queryPort,
        IUserLlmCatalogPort catalogPort,
        IOptions<UserLlmSettingsOptions>? options = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
        _gatewayRouteLabel = NormalizeOptional(options?.Value.GatewayRouteLabel) ?? DefaultGatewayRouteLabel;
    }

    public async Task<UserLlmSettingsView> GetSettingsAsync(string? bearerToken, CancellationToken ct)
    {
        var config = await _queryPort.GetAsync(ct).ConfigureAwait(false);
        return await BuildSettingsViewAsync(config, bearerToken, ct).ConfigureAwait(false);
    }

    private async Task<UserLlmSettingsView> BuildSettingsViewAsync(
        UserConfig config,
        string? bearerToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var savedRoute = UserConfigLlmRoute.Normalize(config.PreferredLlmRoute);
        var defaultModel = NormalizeModel(config.DefaultModel);

        if (string.IsNullOrWhiteSpace(bearerToken))
            return BuildUnavailableSettings(savedRoute, defaultModel);

        try
        {
            var result = await _catalogPort.GetServicesAsync(bearerToken, ct).ConfigureAwait(false);
            (savedRoute, defaultModel) = ResolveLegacyPrefixedModel(result, savedRoute, defaultModel);
            return BuildAvailableSettings(result, savedRoute, defaultModel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return BuildUnavailableSettings(savedRoute, defaultModel);
        }
    }

    private UserLlmSettingsView BuildAvailableSettings(
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
                CanEditModel: true,
                CanSave: canSave,
                CanRetryCatalog: false),
            DefaultModel: defaultModel,
            SetupHint: result.SetupHint);
    }

    private UserLlmSettingsView BuildUnavailableSettings(
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
                Source: NormalizeSource(service.Source).ToWireValue(),
                Status: NormalizeStatus(service.Status).ToWireValue(),
                Allowed: service.Allowed,
                Ready: IsReady(service),
                ServiceId: service.UserServiceId,
                ServiceSlug: service.ServiceSlug,
                Description: NormalizeOptional(service.Description)));
        }

        return options;
    }

    private UserLlmRouteOption BuildGatewayRouteOption(IReadOnlyList<NyxIdLlmService> services)
    {
        var gatewayServices = services.Where(IsGatewayRouteService).ToArray();
        var hasAny = gatewayServices.Length > 0;
        var ready = gatewayServices.Any(IsReady);
        var allowed = !hasAny || gatewayServices.Any(service => service.Allowed);
        var status = !hasAny
            ? UserLlmRouteStatusValue.Ready
            : ready
                ? UserLlmRouteStatusValue.Ready
                : NormalizeStatus(gatewayServices[0].Status);

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

    private static bool IsUserServiceRoute(NyxIdLlmService service)
    {
        var source = NormalizeSource(service.Source);
        return source.IsUserServiceRoute;
    }

    private static bool IsReady(NyxIdLlmService service) =>
        service.Allowed && NormalizeStatus(service.Status).IsReady;

    private string ResolveRouteLabel(string route, IReadOnlyList<UserLlmRouteOption> routeOptions)
    {
        if (string.Equals(route, UserConfigLlmRouteDefaults.Gateway, StringComparison.OrdinalIgnoreCase))
            return _gatewayRouteLabel;

        return routeOptions.FirstOrDefault(option =>
            string.Equals(option.RouteValue, route, StringComparison.OrdinalIgnoreCase))?.Label ?? route;
    }

    private static string NormalizeDisplayName(string? displayName, string fallback)
    {
        var normalized = NormalizeOptional(displayName);
        return normalized ?? fallback.Trim();
    }

    private static UserLlmRouteStatusValue NormalizeStatus(string? status)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized)
            ? UserLlmRouteStatusValue.Unknown
            : new UserLlmRouteStatusValue(normalized);
    }

    private static UserLlmRouteSourceValue NormalizeSource(string? source)
    {
        var normalized = source?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "user" => UserLlmRouteSourceValue.UserService,
            "user_service" => UserLlmRouteSourceValue.UserService,
            "proxy_service" => UserLlmRouteSourceValue.ProxyService,
            "proxy" => UserLlmRouteSourceValue.ProxyService,
            "gateway" => UserLlmRouteSourceValue.GatewayProvider,
            "gateway_provider" => UserLlmRouteSourceValue.GatewayProvider,
            _ => string.IsNullOrWhiteSpace(normalized)
                ? UserLlmRouteSourceValue.UserService
                : new UserLlmRouteSourceValue(normalized),
        };
    }

    private static string NormalizeModel(string? model) => model?.Trim() ?? string.Empty;

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static (string SavedRoute, string DefaultModel) ResolveLegacyPrefixedModel(
        NyxIdLlmServicesResult result,
        string savedRoute,
        string defaultModel)
    {
        if (!string.IsNullOrWhiteSpace(savedRoute) ||
            UserConfigLlmModel.TryParseRouteModel(defaultModel) is not { } prefixed)
        {
            return (savedRoute, defaultModel);
        }

        var prefixedOption = result.Services
            .Select(NyxIdLlmServiceMapping.ToOption)
            .FirstOrDefault(option => IsSameOption(option, prefixed.RouteSlug));
        return prefixedOption is null
            ? (savedRoute, defaultModel)
            : (UserConfigLlmRoute.Normalize(prefixedOption.RouteValue), prefixed.Model);
    }

    private static bool IsSameOption(UserLlmOption option, string requested) =>
        string.Equals(option.ServiceId, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.ServiceSlug, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.DisplayName, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(option.RouteValue, UserConfigLlmRoute.Normalize(requested), StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct UserLlmCatalogStatusValue(string Value)
{
    public static readonly UserLlmCatalogStatusValue Ready = new(UserLlmCatalogStatus.Ready);
    public static readonly UserLlmCatalogStatusValue Empty = new(UserLlmCatalogStatus.Empty);
    public static readonly UserLlmCatalogStatusValue Unavailable = new(UserLlmCatalogStatus.Unavailable);

    public string ToWireValue() => Value;
}

internal readonly record struct UserLlmFallbackReasonValue(string Value)
{
    public static readonly UserLlmFallbackReasonValue CatalogUnavailable = new("catalog_unavailable");
    public static readonly UserLlmFallbackReasonValue SavedRouteUnavailable = new("saved_route_unavailable");

    public string ToWireValue() => Value;
}

internal readonly record struct UserLlmRouteStatusValue(string Value)
{
    public static readonly UserLlmRouteStatusValue Ready = new("ready");
    public static readonly UserLlmRouteStatusValue Unavailable = new("unavailable");
    public static readonly UserLlmRouteStatusValue Unknown = new("unknown");

    public bool IsReady => string.Equals(Value, Ready.Value, StringComparison.OrdinalIgnoreCase);

    public string ToWireValue() => Value;
}

internal readonly record struct UserLlmRouteSourceValue(string Value)
{
    public static readonly UserLlmRouteSourceValue GatewayProvider = new(NyxIdLlmProviderSource.GatewayProvider);
    public static readonly UserLlmRouteSourceValue UserService = new(NyxIdLlmProviderSource.UserService);
    public static readonly UserLlmRouteSourceValue ProxyService = new(NyxIdLlmProviderSource.ProxyService);

    public bool IsUserServiceRoute =>
        string.Equals(Value, UserService.Value, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Value, ProxyService.Value, StringComparison.OrdinalIgnoreCase);

    public string ToWireValue() => Value;
}

public static class NyxIdLlmServiceMapping
{
    public static UserLlmOption ToOption(NyxIdLlmService service) => new(
        ServiceId: NormalizeRequired(service.UserServiceId, nameof(service.UserServiceId)),
        ServiceSlug: NormalizeRequired(service.ServiceSlug, nameof(service.ServiceSlug)),
        DisplayName: NormalizeRequired(service.DisplayName, nameof(service.DisplayName)),
        RouteValue: NormalizeRequired(service.RouteValue, nameof(service.RouteValue)),
        DefaultModel: NormalizeOptional(service.DefaultModel),
        AvailableModels: service.Models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        Status: NormalizeRequired(service.Status, nameof(service.Status)),
        Source: NormalizeRequired(service.Source, nameof(service.Source)),
        Allowed: service.Allowed,
        Description: NormalizeOptional(service.Description));

    private static string NormalizeRequired(string value, string name)
    {
        var normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{name} must not be empty.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
