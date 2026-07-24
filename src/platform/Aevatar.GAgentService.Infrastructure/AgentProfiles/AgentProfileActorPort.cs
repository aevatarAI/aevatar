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
    private readonly AgentProfileIngressProofService _ingressProofService;

    public AgentProfileActorPort(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        AgentProfileIngressProofService ingressProofService)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _ingressProofService = ingressProofService ??
            throw new ArgumentNullException(nameof(ingressProofService));
    }

    public AgentProfileActorTargets ResolveCreateTargets(string profileId)
    {
        var profileActorId = AgentProfileActorIds.Profile(profileId);
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

        RequireSigned(AgentProfileActorIds.Namespace, command);
        var targets = ResolveCreateTargets(command.Identity.ProfileId);
        await EnsureActorAsync<AgentProfileNamespaceGAgent>(targets.NamespaceActorId, ct);
        await EnsureActorAsync<AgentProfileGAgent>(targets.ProfileActorId, ct);
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
        RequireSigned(profileActorId, command);
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

    private void RequireSigned(string targetActorId, IMessage command)
    {
        if (!_ingressProofService.TrySign(targetActorId, command))
            throw new AgentProfileIngressProofUnavailableException();
    }
}
