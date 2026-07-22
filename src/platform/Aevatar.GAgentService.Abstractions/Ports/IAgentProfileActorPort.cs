using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IAgentProfileActorPort
{
    Task<AgentProfileActorTargets> EnsureCreateTargetsAsync(
        string profileId,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchCreateAsync(
        CreateAgentProfileCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchUpdateDraftAsync(
        UpdateAgentProfileDraftCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchUpsertSkillBindingAsync(
        UpsertAgentProfileSkillBindingCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchRemoveSkillBindingAsync(
        RemoveAgentProfileSkillBindingCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchPublishAsync(
        PublishAgentProfileCommand command,
        CancellationToken ct = default);
}
