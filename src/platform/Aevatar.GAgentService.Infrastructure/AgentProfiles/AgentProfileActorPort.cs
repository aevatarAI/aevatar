using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Core.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Infrastructure.AgentProfiles;

public sealed class AgentProfileActorPort : IAgentProfileActorPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public AgentProfileActorPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task<AgentProfileActorTargets> EnsureCreateTargetsAsync(
        string profileId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var profileActorId = AgentProfileActorIds.Profile(profileId);
        await EnsureActorAsync<AgentProfileNamespaceGAgent>(AgentProfileActorIds.Namespace, ct);
        await EnsureActorAsync<AgentProfileGAgent>(profileActorId, ct);
        return new AgentProfileActorTargets(AgentProfileActorIds.Namespace, profileActorId);
    }

    public async Task<DispatchAdmission> DispatchCreateAsync(
        CreateAgentProfileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Identity);
        ArgumentNullException.ThrowIfNull(command.Operation);
        var profileActorId = AgentProfileActorIds.Profile(command.Identity.ProfileId);
        if (!string.Equals(
                command.ProfileActorId,
                profileActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Create Agent Profile command target must match the deterministic Profile Actor target.");
        }

        var targets = await EnsureCreateTargetsAsync(command.Identity.ProfileId, ct);
        return await DispatchAsync(targets.NamespaceActorId, command.Operation, command, ct);
    }

    public Task<DispatchAdmission> DispatchUpdateDraftAsync(
        UpdateAgentProfileDraftCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Identity);
        ArgumentNullException.ThrowIfNull(command.Operation);
        return DispatchProfileAsync(command, command.Identity.ProfileId, command.Operation, ct);
    }

    public Task<DispatchAdmission> DispatchUpsertSkillBindingAsync(
        UpsertAgentProfileSkillBindingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Identity);
        ArgumentNullException.ThrowIfNull(command.Operation);
        return DispatchProfileAsync(command, command.Identity.ProfileId, command.Operation, ct);
    }

    public Task<DispatchAdmission> DispatchRemoveSkillBindingAsync(
        RemoveAgentProfileSkillBindingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Identity);
        ArgumentNullException.ThrowIfNull(command.Operation);
        return DispatchProfileAsync(command, command.Identity.ProfileId, command.Operation, ct);
    }

    public Task<DispatchAdmission> DispatchPublishAsync(
        PublishAgentProfileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Identity);
        ArgumentNullException.ThrowIfNull(command.Operation);
        return DispatchProfileAsync(command, command.Identity.ProfileId, command.Operation, ct);
    }

    private async Task<DispatchAdmission> DispatchProfileAsync(
        IMessage command,
        string profileId,
        AgentProfileOperationFact operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();
        var profileActorId = AgentProfileActorIds.Profile(profileId);
        await EnsureActorAsync<AgentProfileGAgent>(profileActorId, ct);
        return await DispatchAsync(profileActorId, operation, command, ct);
    }

    private async Task EnsureActorAsync<TAgent>(
        string actorId,
        CancellationToken ct)
        where TAgent : IAgent
    {
        ct.ThrowIfCancellationRequested();
        if (await _runtime.GetAsync(actorId) is null)
            await _runtime.CreateAsync<TAgent>(actorId, ct);
    }

    private Task<DispatchAdmission> DispatchAsync(
        string targetActorId,
        AgentProfileOperationFact operation,
        IMessage command,
        CancellationToken ct) =>
        _dispatchPort.DispatchAsync(
            targetActorId,
            AgentProfileEnvelopeFactory.Create(targetActorId, operation, command),
            ct);
}
