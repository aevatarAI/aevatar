namespace Aevatar.Studio.Application.Studio.Abstractions;

public readonly record struct UserLlmCatalogStatusValue(string Value)
{
    public static readonly UserLlmCatalogStatusValue Ready = new(UserLlmCatalogStatus.Ready);
    public static readonly UserLlmCatalogStatusValue Empty = new(UserLlmCatalogStatus.Empty);
    public static readonly UserLlmCatalogStatusValue Unavailable = new(UserLlmCatalogStatus.Unavailable);

    public string ToWireValue() => Value;
}

public readonly record struct UserLlmFallbackReasonValue(string Value)
{
    public static readonly UserLlmFallbackReasonValue CatalogUnavailable = new(UserLlmFallbackReason.CatalogUnavailable);
    public static readonly UserLlmFallbackReasonValue SavedRouteUnavailable = new(UserLlmFallbackReason.SavedRouteUnavailable);

    public string ToWireValue() => Value;
}

public readonly record struct UserLlmRouteStatusValue(string Value)
{
    public static readonly UserLlmRouteStatusValue Ready = new(UserLlmRouteStatus.Ready);
    public static readonly UserLlmRouteStatusValue Unavailable = new(UserLlmRouteStatus.Unavailable);
    public static readonly UserLlmRouteStatusValue Unknown = new(UserLlmRouteStatus.Unknown);

    public bool IsReady => string.Equals(Value, Ready.Value, StringComparison.OrdinalIgnoreCase);

    public string ToWireValue() => Value;
}

public readonly record struct UserLlmRouteSourceValue(string Value)
{
    public static readonly UserLlmRouteSourceValue GatewayProvider = new(UserLlmRouteSource.GatewayProvider);
    public static readonly UserLlmRouteSourceValue UserService = new(UserLlmRouteSource.UserService);
    public static readonly UserLlmRouteSourceValue ProxyService = new(UserLlmRouteSource.ProxyService);
    public static readonly UserLlmRouteSourceValue Unknown = new(UserLlmRouteSource.Unknown);

    public bool IsUserServiceRoute =>
        string.Equals(Value, UserService.Value, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Value, ProxyService.Value, StringComparison.OrdinalIgnoreCase);

    public string ToWireValue() => Value;
}

public static class UserLlmCatalogNormalization
{
    public static UserLlmRouteStatusValue NormalizeStatus(string? status)
    {
        var normalized = status?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "ready" => UserLlmRouteStatusValue.Ready,
            "unavailable" => UserLlmRouteStatusValue.Unavailable,
            "not_connected" => UserLlmRouteStatusValue.Unavailable,
            "disabled" => UserLlmRouteStatusValue.Unavailable,
            "error" => UserLlmRouteStatusValue.Unavailable,
            "missing" => UserLlmRouteStatusValue.Unavailable,
            _ => UserLlmRouteStatusValue.Unknown,
        };
    }

    public static UserLlmRouteSourceValue NormalizeSource(string? source)
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
            _ => UserLlmRouteSourceValue.Unknown,
        };
    }

    public static bool IsReady(NyxIdLlmService service) =>
        service.Allowed && NormalizeStatus(service.Status).IsReady;
}

public static class NyxIdLlmServiceMapping
{
    public static UserLlmOption ToOption(NyxIdLlmService service) => new(
        ServiceSlug: NormalizeRequired(service.ServiceSlug, nameof(service.ServiceSlug)),
        DisplayName: NormalizeRequired(service.DisplayName, nameof(service.DisplayName)),
        RouteValue: NormalizeRequired(service.RouteValue, nameof(service.RouteValue)),
        DefaultModel: NormalizeOptional(service.DefaultModel),
        AvailableModels: service.Models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        Status: UserLlmCatalogNormalization.NormalizeStatus(service.Status).ToWireValue(),
        Source: UserLlmCatalogNormalization.NormalizeSource(service.Source).ToWireValue(),
        Allowed: service.Allowed,
        Description: NormalizeOptional(service.Description),
        Identity: service.Identity);

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
