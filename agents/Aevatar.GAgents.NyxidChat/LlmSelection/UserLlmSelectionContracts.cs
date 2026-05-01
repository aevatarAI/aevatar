using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed record UserLlmOptionsQuery(
    BindingId BindingId,
    ExternalSubjectRef Subject,
    string RegistrationScopeId);

public sealed record UserLlmSelectionContext(
    BindingId BindingId,
    ExternalSubjectRef Subject,
    string RegistrationScopeId);

public sealed record UserLlmOptionsView(
    BindingId BindingId,
    UserLlmOption? Current,
    IReadOnlyList<UserLlmOption> Available,
    UserLlmSetupHint? SetupHint);

public sealed record UserLlmOption(
    string ServiceId,
    string ServiceSlug,
    string DisplayName,
    string RouteValue,
    string? DefaultModel,
    IReadOnlyList<string> AvailableModels,
    string Status,
    string Source,
    bool Allowed,
    string? Description);

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
    string ServiceId,
    string RouteValue,
    string? DefaultModel) : UserLlmPresetActivation;

public sealed record ProvisionThenUse(string ProvisionEndpointId) : UserLlmPresetActivation;

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

public interface IUserLlmOptionsService
{
    Task<UserLlmOptionsView> GetOptionsAsync(UserLlmOptionsQuery query, CancellationToken ct);
}

public interface IUserLlmSelectionService
{
    Task SetByServiceAsync(
        UserLlmSelectionContext context,
        string serviceId,
        string? modelOverride,
        CancellationToken ct);

    Task SetModelOverrideAsync(
        UserLlmSelectionContext context,
        string model,
        CancellationToken ct);

    Task ApplyPresetAsync(
        UserLlmSelectionContext context,
        string presetId,
        CancellationToken ct);

    Task ResetAsync(UserLlmSelectionContext context, CancellationToken ct);
}

public interface INyxIdLlmServiceCatalogClient
{
    Task<IReadOnlyList<NyxIdLlmService>> ListAsync(
        UserLlmOptionsQuery query,
        CancellationToken ct);

    Task<UserLlmSetupHint> GetSetupHintAsync(
        UserLlmOptionsQuery query,
        CancellationToken ct);
}

public interface IUserLlmOptionsRenderer<TChannelMessage>
{
    TChannelMessage RenderOptions(UserLlmOptionsView view);

    TChannelMessage RenderSelectionConfirm(UserLlmOption picked, string? model);

    TChannelMessage RenderSetupGuide(UserLlmSetupHint hint);

    TChannelMessage RenderPresetProvisioning(UserLlmPreset preset);
}
