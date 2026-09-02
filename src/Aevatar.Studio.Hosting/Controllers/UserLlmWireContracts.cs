using Aevatar.AI.Abstractions;
using System.Text.Json.Serialization;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.Studio.Hosting.Controllers;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveUserLlmSettingsRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("gateway")] SelectGatewayRequest? Gateway = null,
    [property: JsonPropertyName("userService")] SelectUserServiceRequest? UserService = null,
    [property: JsonPropertyName("preset")] ActivatePresetRequest? Preset = null)
{
    public UserLlmPreferenceIntent ToIntent() => Action switch
    {
        "reset" when Gateway is null && UserService is null && Preset is null =>
            new ResetUserLlmPreferenceIntent(),
        "select_gateway" when Gateway is not null && UserService is null && Preset is null =>
            new SelectGatewayUserLlmPreferenceIntent(Gateway.RequireModelSelection()),
        "select_user_service" when Gateway is null && UserService is not null && Preset is null =>
            UserService.ToIntent(),
        "activate_preset" when Gateway is null && UserService is null && Preset is not null =>
            Preset.ToIntent(),
        "reset" or "select_gateway" or "select_user_service" or "activate_preset" =>
            throw new InvalidOperationException("The LLM action payload does not match its action."),
        _ => throw new InvalidOperationException("Unknown LLM settings action."),
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelectGatewayRequest(
    [property: JsonPropertyName("model")] UserLlmModelSelectionRequest? Model)
{
    public LLMModelSelection RequireModelSelection() =>
        Model?.ToApplication() ?? throw new InvalidOperationException("Gateway model selection is required.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SelectUserServiceRequest(
    [property: JsonPropertyName("userServiceId")] string UserServiceId,
    [property: JsonPropertyName("model")] UserLlmModelSelectionRequest? Model)
{
    public SelectUserServiceUserLlmPreferenceIntent ToIntent()
    {
        var id = UserServiceId?.Trim();
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("userServiceId is required.");

        return new SelectUserServiceUserLlmPreferenceIntent(
            id,
            Model?.ToApplication() ??
            throw new InvalidOperationException("User service model selection is required."));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ActivatePresetRequest(
    [property: JsonPropertyName("presetId")] string PresetId)
{
    public ActivateUserLlmPresetIntent ToIntent()
    {
        var id = PresetId?.Trim();
        return !string.IsNullOrEmpty(id)
            ? new ActivateUserLlmPresetIntent(id)
            : throw new InvalidOperationException("presetId is required.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UserLlmModelSelectionRequest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("modelId")] string? ModelId = null)
{
    public LLMModelSelection ToApplication()
    {
        var selection = Kind switch
        {
            "provider_default" when ModelId is null => new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ProviderDefault,
            },
            "explicit_model" => new LLMModelSelection
            {
                Kind = LLMModelSelectionKind.ExplicitModel,
                ModelId = ModelId ?? string.Empty,
            },
            "provider_default" => throw new InvalidOperationException(
                "provider_default cannot include modelId."),
            _ => throw new InvalidOperationException("Unknown LLM model selection kind."),
        };

        // Reuse the shared route/model validator for canonical model ID validation.
        _ = UserLlmPreferenceWriteCore.BuildGatewaySelection(selection);
        return selection;
    }
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
    [property: JsonPropertyName("savedSelection")] UserLlmSelectionResponse? SavedSelection,
    [property: JsonPropertyName("savedRouteLabel")] string SavedRouteLabel,
    [property: JsonPropertyName("selectionStatus")] string SelectionStatus,
    [property: JsonPropertyName("catalogDiagnostic")] string CatalogDiagnostic,
    [property: JsonPropertyName("remediation")] string Remediation,
    [property: JsonPropertyName("routeOptions")] IReadOnlyList<UserLlmRouteOptionResponse> RouteOptions,
    [property: JsonPropertyName("modelGroupsByRoute")] IReadOnlyList<UserLlmModelGroupResponse> ModelGroupsByRoute,
    [property: JsonPropertyName("catalogStatus")] string CatalogStatus,
    [property: JsonPropertyName("capabilities")] UserLlmSettingsCapabilitiesResponse Capabilities,
    [property: JsonPropertyName("setupHint")] UserLlmSetupHintResponse? SetupHint)
{
    public static UserLlmSettingsResponse FromApplication(UserLlmSettingsView view) => new(
        view.SavedSelection is null ? null : UserLlmSelectionResponse.FromApplication(view.SavedSelection),
        view.SavedRouteLabel,
        view.SelectionStatus.ToWireValue(),
        view.CatalogDiagnostic.ToWireValue(),
        view.Remediation.ToWireValue(),
        view.RouteOptions.Select(UserLlmRouteOptionResponse.FromApplication).ToArray(),
        view.ModelGroupsByRoute.Select(UserLlmModelGroupResponse.FromApplication).ToArray(),
        view.CatalogStatus,
        UserLlmSettingsCapabilitiesResponse.FromApplication(view.Capabilities),
        view.SetupHint is null ? null : UserLlmSetupHintResponse.FromApplication(view.SetupHint));
}

public sealed record UserLlmSelectionResponse(
    [property: JsonPropertyName("routeKind")] string RouteKind,
    [property: JsonPropertyName("routeValue")] string RouteValue,
    [property: JsonPropertyName("nyxIdUserServiceId")] string NyxIdUserServiceId,
    [property: JsonPropertyName("serviceSlugSnapshot")] string ServiceSlugSnapshot,
    [property: JsonPropertyName("modelSelection")] UserLlmModelSelectionResponse? ModelSelection)
{
    public static UserLlmSelectionResponse FromApplication(LLMSelection selection) => new(
        selection.RouteKind switch
        {
            LLMRouteKind.Unspecified => "unspecified",
            LLMRouteKind.Gateway => "gateway",
            LLMRouteKind.NyxIdUserService => "nyx_id_user_service",
            _ => "unsupported",
        },
        selection.RouteValue,
        selection.NyxIdUserServiceId,
        selection.ServiceSlugSnapshot,
        selection.ModelSelection is null
            ? null
            : UserLlmModelSelectionResponse.FromApplication(selection.ModelSelection));
}

public sealed record UserLlmModelSelectionResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("modelId")] string? ModelId)
{
    public static UserLlmModelSelectionResponse FromApplication(LLMModelSelection selection) => new(
        selection.Kind switch
        {
            LLMModelSelectionKind.Unspecified => "unspecified",
            LLMModelSelectionKind.ProviderDefault => "provider_default",
            LLMModelSelectionKind.ExplicitModel => "explicit_model",
            _ => "unsupported",
        },
        selection.Kind == LLMModelSelectionKind.ExplicitModel ? selection.ModelId : null);
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
    [property: JsonPropertyName("modelCatalog")] UserLlmModelCatalogResponse ModelCatalog,
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
        UserLlmModelCatalogResponse.FromApplication(option.ModelCatalog),
        option.Description);
}

public sealed record UserLlmModelCatalogResponse(
    [property: JsonPropertyName("certainty")] string Certainty,
    [property: JsonPropertyName("modelIds")] IReadOnlyList<string> ModelIds,
    [property: JsonPropertyName("defaultModelId")] string? DefaultModelId,
    [property: JsonPropertyName("diagnostic")] string Diagnostic)
{
    public static UserLlmModelCatalogResponse FromApplication(LLMModelCatalog catalog) => new(
        catalog.Certainty.ToWireValue(),
        catalog.ModelIds.ToArray(),
        string.IsNullOrEmpty(catalog.DefaultModelId) ? null : catalog.DefaultModelId,
        catalog.DiagnosticKind.ToWireValue());
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
