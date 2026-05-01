using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Studio.Application.Studio.Abstractions;

namespace Aevatar.GAgents.NyxidChat.LlmSelection;

public sealed record UserLlmOptionsQuery(
    BindingId BindingId,
    ExternalSubjectRef Subject,
    string RegistrationScopeId);

public sealed record UserLlmSelectionContext(
    BindingId BindingId,
    ExternalSubjectRef Subject,
    string RegistrationScopeId);

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
    Task<NyxIdLlmServicesResult> GetServicesAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct);

    Task<UserLlmSetupHint> GetSetupHintAsync(
        UserLlmOptionsQuery query,
        string accessToken,
        CancellationToken ct);

    Task<NyxIdLlmService> ProvisionAsync(
        UserLlmSelectionContext context,
        string accessToken,
        string provisionEndpointId,
        CancellationToken ct);
}

public interface IUserLlmOptionsRenderer<TChannelMessage>
{
    TChannelMessage RenderOptions(UserLlmOptionsView view);

    TChannelMessage RenderSelectionConfirm(UserLlmOption picked, string? model);

    TChannelMessage RenderSetupGuide(UserLlmSetupHint hint);

    TChannelMessage RenderPresetProvisioning(UserLlmPreset preset);
}
