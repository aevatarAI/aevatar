using Aevatar.AI.Abstractions;
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

public enum UserLlmSelectionDisplayMode
{
    Model = 0,
    Route = 1,
}

public interface IUserLlmSelectionService
{
    Task SetByServiceAsync(
        UserLlmSelectionContext context,
        string userServiceId,
        LLMModelSelection modelSelection,
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
    TChannelMessage RenderCurrent(UserLlmOptionsView view, UserLlmSelectionDisplayMode mode);

    TChannelMessage RenderOptions(UserLlmOptionsView view, UserLlmSelectionDisplayMode mode, int page = 1);

    TChannelMessage RenderSelectionConfirm(UserLlmOption picked, LLMModelSelection modelSelection);

    TChannelMessage RenderSetupGuide(UserLlmSetupHint hint);

    TChannelMessage RenderPresetProvisioning(UserLlmPreset preset);
}
