using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

/// <summary>
/// Accepted-only command dispatch boundary for Agent Profile authority actors.
/// </summary>
public interface IAgentProfileActorPort
{
    Task<DispatchAdmission> DispatchCreateAsync(
        CreateAgentProfileCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchInitializeAsync(
        string profileActorId,
        InitializeAgentProfileCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchUpdateDraftAsync(
        string profileActorId,
        UpdateAgentProfileDraftCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchPublishAsync(
        string profileActorId,
        PublishAgentProfileCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchSetDefaultBindingAsync(
        SetAgentProfileDefaultBindingCommand command,
        CancellationToken ct = default);

    Task<DispatchAdmission> DispatchClearDefaultBindingAsync(
        ClearAgentProfileDefaultBindingCommand command,
        CancellationToken ct = default);
}
