using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public readonly record struct UserLlmCatalogStatusValue(string Value)
{
    public static readonly UserLlmCatalogStatusValue Ready = new(UserLlmCatalogStatus.Ready);
    public static readonly UserLlmCatalogStatusValue Empty = new(UserLlmCatalogStatus.Empty);
    public static readonly UserLlmCatalogStatusValue Unavailable = new(UserLlmCatalogStatus.Unavailable);

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
    public static string ToWireValue(this UserLlmSelectionStatus status) => status switch
    {
        UserLlmSelectionStatus.SystemDefault => "system_default",
        UserLlmSelectionStatus.Ready => "ready",
        UserLlmSelectionStatus.VerificationUnavailable => "verification_unavailable",
        UserLlmSelectionStatus.NeedsRepair => "needs_repair",
        UserLlmSelectionStatus.LegacyRepairRequired => "legacy_repair_required",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string ToWireValue(this UserLlmRemediationKind remediation) => remediation switch
    {
        UserLlmRemediationKind.None => "none",
        UserLlmRemediationKind.RetryCatalog => "retry_catalog",
        UserLlmRemediationKind.ConnectProvider => "connect_provider",
        UserLlmRemediationKind.ChooseReplacement => "choose_replacement",
        UserLlmRemediationKind.Reselect => "reselect",
        _ => throw new ArgumentOutOfRangeException(nameof(remediation)),
    };

    public static string ToWireValue(this LLMModelCatalogDiagnosticKind diagnostic) => diagnostic switch
    {
        LLMModelCatalogDiagnosticKind.Unspecified => "unspecified",
        LLMModelCatalogDiagnosticKind.NotPublished => "not_published",
        LLMModelCatalogDiagnosticKind.RouteNotReady => "route_not_ready",
        LLMModelCatalogDiagnosticKind.AccessDenied => "access_denied",
        LLMModelCatalogDiagnosticKind.ObservationUnavailable => "observation_unavailable",
        LLMModelCatalogDiagnosticKind.ResponseInvalid => "response_invalid",
        LLMModelCatalogDiagnosticKind.ResponseTooLarge => "response_too_large",
        LLMModelCatalogDiagnosticKind.PatternOnly => "pattern_only",
        _ => throw new ArgumentOutOfRangeException(nameof(diagnostic)),
    };

    public static string ToWireValue(this LLMModelCatalogCertainty certainty) => certainty switch
    {
        LLMModelCatalogCertainty.Enumerated => "enumerated",
        LLMModelCatalogCertainty.NotVerifiable => "not_verifiable",
        LLMModelCatalogCertainty.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(certainty)),
    };

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
        ModelCatalog: CloneValidated(service.ModelCatalog),
        Status: UserLlmCatalogNormalization.NormalizeStatus(service.Status).ToWireValue(),
        Source: UserLlmCatalogNormalization.NormalizeSource(service.Source).ToWireValue(),
        Allowed: service.Allowed,
        Description: NormalizeOptional(service.Description),
        Identity: service.Identity);

    private static LLMModelCatalog CloneValidated(LLMModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        LLMSelectionPolicy.ValidateCatalog(catalog);
        return catalog.Clone();
    }

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
