namespace Aevatar.Studio.Application.Studio.Abstractions;

/// <summary>
/// Internal write-use-case command shared by Console Settings and channel /model selection.
/// </summary>
public sealed record SaveUserLlmPreferenceCommand(
    string? UserServiceId = null,
    string? RouteValue = null,
    string? Model = null,
    string? PresetId = null,
    bool? Reset = null);

public sealed record UserLlmSettingsView(
    string SavedRoute,
    string SavedRouteLabel,
    string SavedRouteKind,
    string? SavedUserServiceId,
    string? SavedServiceSlug,
    string EffectiveRoute,
    string EffectiveRouteLabel,
    bool RouteFallbackActive,
    string? FallbackReason,
    IReadOnlyList<UserLlmRouteOption> RouteOptions,
    IReadOnlyList<UserLlmModelGroup> ModelGroupsByRoute,
    string CatalogStatus,
    UserLlmSettingsCapabilities Capabilities,
    string DefaultModel,
    UserLlmSetupHint? SetupHint);

public sealed record UserLlmRouteOption(
    string RouteValue,
    string Label,
    string Source,
    string Status,
    bool Allowed,
    bool Ready,
    string? UserServiceId,
    string? ServiceSlug,
    string? DefaultModel,
    string? Description);

public sealed record UserLlmModelGroup(
    string RouteValue,
    string GroupId,
    string Label,
    IReadOnlyList<string> Models);

public sealed record UserLlmSettingsCapabilities(
    bool CanEditRoute,
    bool CanEditModel,
    bool CanSave,
    bool CanRetryCatalog);

public sealed class UserLlmSettingsOptions
{
    public string GatewayRouteLabel { get; set; } = "NyxID Gateway";
}

public static class UserLlmCatalogStatus
{
    public const string Ready = "ready";
    public const string Empty = "empty";
    public const string Unavailable = "unavailable";
}

public static class UserLlmFallbackReason
{
    public const string CatalogUnavailable = "catalog_unavailable";
    public const string SavedRouteUnavailable = "saved_route_unavailable";
}

public static class UserLlmRouteStatus
{
    public const string Ready = "ready";
    public const string Unavailable = "unavailable";
    public const string Unknown = "unknown";
}

public static class UserLlmRouteSource
{
    public const string GatewayProvider = NyxIdLlmProviderSource.GatewayProvider;
    public const string UserService = NyxIdLlmProviderSource.UserService;
    public const string ProxyService = NyxIdLlmProviderSource.ProxyService;
    public const string Unknown = "unknown";
}

/// <summary>
/// Channel interaction contract for NyxID /model selection. Console Settings uses
/// <see cref="UserLlmSettingsView"/> as its only public settings view.
/// </summary>
public sealed record UserLlmOptionsView(
    UserLlmOption? Current,
    IReadOnlyList<UserLlmOption> Available,
    UserLlmSetupHint? SetupHint)
{
    public static readonly UserLlmOptionsView Empty = new(null, [], null);

    public string? CurrentRouteValue { get; init; }

    public string? CurrentModel { get; init; }
}

/// <summary>
/// Routable LLM option used by channel selection flows and internal preference resolution,
/// not by the Console Settings endpoint contract.
/// </summary>
public enum UserLlmIdentityAuthority
{
    Unspecified = 0,
    NyxIdUserServicesInventory = 1,
}

public sealed record UserLlmServiceIdentity(
    UserLlmIdentityAuthority Authority,
    string NyxIdUserServiceId);

public sealed record UserLlmOption(
    string ServiceSlug,
    string DisplayName,
    string RouteValue,
    string? DefaultModel,
    IReadOnlyList<string> AvailableModels,
    string Status,
    string Source,
    bool Allowed,
    string? Description,
    UserLlmServiceIdentity? Identity = null);

public sealed record UserLlmSetupHint(
    string SetupUrl,
    IReadOnlyList<UserLlmPreset> Presets);

public sealed record UserLlmPreset(
    string Id,
    string Title,
    string Description,
    UserLlmPresetActivation Activation);

public abstract record UserLlmPresetActivation;

public sealed record UseExistingService(
    string UserServiceId,
    string RouteValue,
    string? DefaultModel)
    : UserLlmPresetActivation;

public sealed record ProvisionThenUse(
    string ProvisionEndpointId)
    : UserLlmPresetActivation;

public sealed record NyxIdLlmService(
    string? CatalogEntryId,
    string ServiceSlug,
    string DisplayName,
    string RouteValue,
    string? DefaultModel,
    IReadOnlyList<string> Models,
    string Status,
    string Source,
    bool Allowed,
    string? Description,
    UserLlmServiceIdentity? Identity = null);

public sealed record NyxIdLlmServicesResult(
    IReadOnlyList<NyxIdLlmService> Services,
    UserLlmSetupHint? SetupHint);

public static class NyxIdLlmProviderSource
{
    public const string GatewayProvider = "gateway_provider";
    public const string UserService = "user_service";
    public const string ProxyService = "proxy_service";
}

public interface IUserLlmCatalogPort
{
    Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct);

    Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct);
}

public interface IUserLlmPreferenceService
{
    Task<UserLlmSettingsView> GetSettingsAsync(string? bearerToken, CancellationToken ct);
}

public interface IChannelUserLlmPreferencePort
{
    Task<UserConfigSaveReceipt> SaveAsync(
        string bindingId,
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct);

    Task<UserConfigSaveReceipt> SaveSelectedOptionAsync(
        string bindingId,
        UserLlmOption option,
        string? model,
        bool preserveCurrentModelWhenMissing,
        CancellationToken ct);
}
