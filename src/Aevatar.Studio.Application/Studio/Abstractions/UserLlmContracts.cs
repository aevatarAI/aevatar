using System.Text.Json.Serialization;

namespace Aevatar.Studio.Application.Studio.Abstractions;

public sealed record SaveUserLlmPreferenceCommand(
    string? ServiceId = null,
    string? RouteValue = null,
    string? Model = null,
    string? PresetId = null,
    bool? Reset = null);

public sealed record UserLlmOptionsView(
    [property: JsonPropertyName("current")] UserLlmOption? Current,
    [property: JsonPropertyName("available")] IReadOnlyList<UserLlmOption> Available,
    [property: JsonPropertyName("setupHint")] UserLlmSetupHint? SetupHint)
{
    public static readonly UserLlmOptionsView Empty = new(null, [], null);
}

public sealed record UserLlmOption(
    [property: JsonPropertyName("serviceId")] string ServiceId,
    [property: JsonPropertyName("serviceSlug")] string ServiceSlug,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("routeValue")] string RouteValue,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel,
    [property: JsonPropertyName("availableModels")] IReadOnlyList<string> AvailableModels,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("description")] string? Description);

public sealed record UserLlmSetupHint(
    [property: JsonPropertyName("setupUrl")] string SetupUrl,
    [property: JsonPropertyName("presets")] IReadOnlyList<UserLlmPreset> Presets);

public sealed record UserLlmPreset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("activation")] UserLlmPresetActivation Activation);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UseExistingService), "use_existing_service")]
[JsonDerivedType(typeof(ProvisionThenUse), "provision_then_use")]
public abstract record UserLlmPresetActivation;

public sealed record UseExistingService(
    [property: JsonPropertyName("serviceId")] string ServiceId,
    [property: JsonPropertyName("routeValue")] string RouteValue,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel)
    : UserLlmPresetActivation;

public sealed record ProvisionThenUse(
    [property: JsonPropertyName("provisionEndpointId")] string ProvisionEndpointId)
    : UserLlmPresetActivation;

public sealed record NyxIdLlmService(
    string UserServiceId,
    string ServiceSlug,
    string DisplayName,
    string RouteValue,
    string? DefaultModel,
    IReadOnlyList<string> Models,
    string Status,
    string Source,
    bool Allowed,
    string? Description);

public sealed record NyxIdLlmServicesResult(
    IReadOnlyList<NyxIdLlmService> Services,
    UserLlmSetupHint? SetupHint);

public static class NyxIdLlmProviderSource
{
    public const string GatewayProvider = "gateway_provider";
    public const string UserService = "user_service";
}

public interface IUserLlmCatalogPort
{
    Task<NyxIdLlmServicesResult> GetServicesAsync(string bearerToken, CancellationToken ct);

    Task<NyxIdLlmService> ProvisionAsync(string bearerToken, string provisionEndpointId, CancellationToken ct);
}

public interface IUserLlmPreferenceService
{
    Task<UserLlmOptionsView> GetOptionsAsync(string? bearerToken, CancellationToken ct);

    Task<UserConfig> SaveAsync(
        string? bearerToken,
        SaveUserLlmPreferenceCommand command,
        CancellationToken ct);
}
