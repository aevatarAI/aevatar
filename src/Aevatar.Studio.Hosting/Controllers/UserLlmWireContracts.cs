using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Hosting.Controllers;

public sealed record SaveUserLlmSettingsRequest(
    [property: JsonPropertyName("userServiceId")] string? UserServiceId = null,
    [property: JsonPropertyName("routeValue")] string? RouteValue = null,
    [property: JsonPropertyName("model")] string? Model = null)
{
    public SaveUserLlmPreferenceCommand ToCommand() => new(
        UserServiceId: UserServiceId,
        RouteValue: RouteValue,
        Model: Model);
}

public sealed record UserConfigSaveReceiptResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("ackStage")] string AckStage,
    [property: JsonPropertyName("actorId")] string ActorId,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("ackedAtUtc")] DateTimeOffset AckedAtUtc)
{
    public static UserConfigSaveReceiptResponse FromApplication(UserConfigSaveReceipt receipt) => new(
        receipt.Accepted,
        receipt.CommandId,
        receipt.AckStage,
        receipt.ActorId,
        receipt.CorrelationId,
        receipt.AckedAtUtc);
}

public sealed record UserLlmSettingsResponse(
    [property: JsonPropertyName("savedRoute")] string SavedRoute,
    [property: JsonPropertyName("savedRouteLabel")] string SavedRouteLabel,
    [property: JsonPropertyName("savedRouteKind")] string SavedRouteKind,
    [property: JsonPropertyName("savedUserServiceId")] string? SavedUserServiceId,
    [property: JsonPropertyName("savedServiceSlug")] string? SavedServiceSlug,
    [property: JsonPropertyName("effectiveRoute")] string EffectiveRoute,
    [property: JsonPropertyName("effectiveRouteLabel")] string EffectiveRouteLabel,
    [property: JsonPropertyName("routeFallbackActive")] bool RouteFallbackActive,
    [property: JsonPropertyName("fallbackReason")] string? FallbackReason,
    [property: JsonPropertyName("routeOptions")] IReadOnlyList<UserLlmRouteOptionResponse> RouteOptions,
    [property: JsonPropertyName("modelGroupsByRoute")] IReadOnlyList<UserLlmModelGroupResponse> ModelGroupsByRoute,
    [property: JsonPropertyName("catalogStatus")] string CatalogStatus,
    [property: JsonPropertyName("capabilities")] UserLlmSettingsCapabilitiesResponse Capabilities,
    [property: JsonPropertyName("defaultModel")] string DefaultModel,
    [property: JsonPropertyName("setupHint")] UserLlmSetupHintResponse? SetupHint)
{
    public static UserLlmSettingsResponse FromApplication(UserLlmSettingsView view) => new(
        view.SavedRoute,
        view.SavedRouteLabel,
        view.SavedRouteKind,
        view.SavedUserServiceId,
        view.SavedServiceSlug,
        view.EffectiveRoute,
        view.EffectiveRouteLabel,
        view.RouteFallbackActive,
        view.FallbackReason,
        view.RouteOptions.Select(UserLlmRouteOptionResponse.FromApplication).ToArray(),
        view.ModelGroupsByRoute.Select(UserLlmModelGroupResponse.FromApplication).ToArray(),
        view.CatalogStatus,
        UserLlmSettingsCapabilitiesResponse.FromApplication(view.Capabilities),
        view.DefaultModel,
        view.SetupHint is null ? null : UserLlmSetupHintResponse.FromApplication(view.SetupHint));
}

public sealed record UserLlmRouteOptionResponse(
    [property: JsonPropertyName("routeValue")] string RouteValue,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("userServiceId")] string? UserServiceId,
    [property: JsonPropertyName("serviceSlug")] string? ServiceSlug,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel,
    [property: JsonPropertyName("description")] string? Description)
{
    public static UserLlmRouteOptionResponse FromApplication(UserLlmRouteOption option) => new(
        option.RouteValue,
        option.Label,
        option.Source,
        option.Status,
        option.Allowed,
        option.Ready,
        option.UserServiceId,
        option.ServiceSlug,
        option.DefaultModel,
        option.Description);
}

public sealed record UserLlmModelGroupResponse(
    [property: JsonPropertyName("routeValue")] string RouteValue,
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("models")] IReadOnlyList<string> Models)
{
    public static UserLlmModelGroupResponse FromApplication(UserLlmModelGroup group) => new(
        group.RouteValue,
        group.GroupId,
        group.Label,
        group.Models);
}

public sealed record UserLlmSettingsCapabilitiesResponse(
    [property: JsonPropertyName("canEditRoute")] bool CanEditRoute,
    [property: JsonPropertyName("canEditModel")] bool CanEditModel,
    [property: JsonPropertyName("canSave")] bool CanSave,
    [property: JsonPropertyName("canRetryCatalog")] bool CanRetryCatalog)
{
    public static UserLlmSettingsCapabilitiesResponse FromApplication(UserLlmSettingsCapabilities capabilities) => new(
        capabilities.CanEditRoute,
        capabilities.CanEditModel,
        capabilities.CanSave,
        capabilities.CanRetryCatalog);
}

public sealed record UserLlmSetupHintResponse(
    [property: JsonPropertyName("setupUrl")] string SetupUrl,
    [property: JsonPropertyName("presets")] IReadOnlyList<UserLlmPresetResponse> Presets)
{
    public static UserLlmSetupHintResponse FromApplication(UserLlmSetupHint hint) => new(
        hint.SetupUrl,
        hint.Presets.Select(UserLlmPresetResponse.FromApplication).ToArray());
}

public sealed record UserLlmPresetResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("activation")] UserLlmPresetActivationResponse Activation)
{
    public static UserLlmPresetResponse FromApplication(UserLlmPreset preset) => new(
        preset.Id,
        preset.Title,
        preset.Description,
        UserLlmPresetActivationResponse.FromApplication(preset.Activation));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UseExistingServiceResponse), "use_existing_service")]
[JsonDerivedType(typeof(ProvisionThenUseResponse), "provision_then_use")]
public abstract record UserLlmPresetActivationResponse
{
    public static UserLlmPresetActivationResponse FromApplication(UserLlmPresetActivation activation) =>
        activation switch
        {
            UseExistingService existing => new UseExistingServiceResponse(
                existing.UserServiceId,
                existing.RouteValue,
                existing.DefaultModel),
            ProvisionThenUse provisioning => new ProvisionThenUseResponse(provisioning.ProvisionEndpointId),
            _ => throw new InvalidOperationException($"Unsupported LLM preset activation '{activation.GetType().Name}'."),
        };
}

public sealed record UseExistingServiceResponse(
    [property: JsonPropertyName("userServiceId")] string UserServiceId,
    [property: JsonPropertyName("routeValue")] string RouteValue,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel)
    : UserLlmPresetActivationResponse;

public sealed record ProvisionThenUseResponse(
    [property: JsonPropertyName("provisionEndpointId")] string ProvisionEndpointId)
    : UserLlmPresetActivationResponse;
