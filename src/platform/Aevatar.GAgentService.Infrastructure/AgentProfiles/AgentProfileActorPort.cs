using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Aevatar.GAgentService.Core.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Infrastructure.AgentProfiles;

public sealed class AgentProfileActorPort : IAgentProfileActorPort
{
    private readonly IActorRuntime _runtime;
    private readonly IActorDispatchPort _dispatchPort;

    public AgentProfileActorPort(IActorRuntime runtime, IActorDispatchPort dispatchPort)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
    }

    public async Task EnsureCreateTargetsAsync(
        AgentProfileOwner owner,
        string profileId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ct.ThrowIfCancellationRequested();

        await EnsureTargetAsync<AgentProfileNamespaceGAgent>(AgentProfileActorIds.Namespace(owner), ct);
        await EnsureTargetAsync<AgentProfileGAgent>(AgentProfileActorIds.Profile(profileId), ct);
    }

    public async Task<DispatchAdmission> DispatchCreateAsync(
        CreateAgentProfileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await EnsureCreateTargetsAsync(command.Owner, command.ProfileId, ct);
        return await DispatchAsync(AgentProfileActorIds.Namespace(command.Owner), command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchInitializeAsync(
        string profileActorId,
        InitializeAgentProfileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await EnsureProfileTargetAsync(profileActorId, ct);
        return await DispatchAsync(profileActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchUpdateDraftAsync(
        string profileActorId,
        UpdateAgentProfileDraftCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await EnsureProfileTargetAsync(profileActorId, ct);
        return await DispatchAsync(profileActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchPublishAsync(
        string profileActorId,
        PublishAgentProfileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await EnsureProfileTargetAsync(profileActorId, ct);
        return await DispatchAsync(profileActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchSetDefaultBindingAsync(
        SetAgentProfileDefaultBindingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var namespaceActorId = await EnsureNamespaceTargetAsync(command.Owner, ct);
        return await DispatchAsync(namespaceActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchClearDefaultBindingAsync(
        ClearAgentProfileDefaultBindingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var namespaceActorId = await EnsureNamespaceTargetAsync(command.Owner, ct);
        return await DispatchAsync(namespaceActorId, command.Operation, command, ct);
    }

    private async Task EnsureProfileTargetAsync(string profileActorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileActorId);
        await EnsureTargetAsync<AgentProfileGAgent>(profileActorId, ct);
    }

    private async Task<string> EnsureNamespaceTargetAsync(AgentProfileOwner owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var namespaceActorId = AgentProfileActorIds.Namespace(owner);
        await EnsureTargetAsync<AgentProfileNamespaceGAgent>(namespaceActorId, ct);
        return namespaceActorId;
    }

    private async Task EnsureTargetAsync<TAgent>(string actorId, CancellationToken ct)
        where TAgent : IAgent
    {
        var actor = await _runtime.GetAsync(actorId);
        if (actor is null)
            actor = await _runtime.CreateAsync<TAgent>(actorId, ct);

        await actor.ActivateAsync(ct);
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
