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

    public async Task<DispatchAdmission> DispatchCreateAsync(
        CreateAgentProfileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AgentProfileEnvelopeFactory.ValidateOperation(command.Operation);
        var namespaceActorId = ResolveNamespaceActorId(command.Owner);
        var profileActorId = ResolveProfileActorId(command.ProfileId);
        await EnsureTargetAsync<AgentProfileNamespaceGAgent>(namespaceActorId, ct);
        await EnsureTargetAsync<AgentProfileGAgent>(profileActorId, ct);
        return await DispatchAsync(namespaceActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchInitializeAsync(
        string profileActorId,
        InitializeAgentProfileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AgentProfileEnvelopeFactory.ValidateOperation(command.Operation);
        var expectedProfileActorId = ResolveProfileActorId(command.Identity, profileActorId);
        await EnsureTargetAsync<AgentProfileGAgent>(expectedProfileActorId, ct);
        return await DispatchAsync(expectedProfileActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchUpdateDraftAsync(
        string profileActorId,
        UpdateAgentProfileDraftCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AgentProfileEnvelopeFactory.ValidateOperation(command.Operation);
        var expectedProfileActorId = ResolveProfileActorId(command.Identity, profileActorId);
        await EnsureTargetAsync<AgentProfileGAgent>(expectedProfileActorId, ct);
        return await DispatchAsync(expectedProfileActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchPublishAsync(
        string profileActorId,
        PublishAgentProfileCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AgentProfileEnvelopeFactory.ValidateOperation(command.Operation);
        var expectedProfileActorId = ResolveProfileActorId(command.Identity, profileActorId);
        await EnsureTargetAsync<AgentProfileGAgent>(expectedProfileActorId, ct);
        return await DispatchAsync(expectedProfileActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchSetDefaultBindingAsync(
        SetAgentProfileDefaultBindingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AgentProfileEnvelopeFactory.ValidateOperation(command.Operation);
        var namespaceActorId = ResolveNamespaceActorId(command.Owner);
        await EnsureTargetAsync<AgentProfileNamespaceGAgent>(namespaceActorId, ct);
        return await DispatchAsync(namespaceActorId, command.Operation, command, ct);
    }

    public async Task<DispatchAdmission> DispatchClearDefaultBindingAsync(
        ClearAgentProfileDefaultBindingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        AgentProfileEnvelopeFactory.ValidateOperation(command.Operation);
        var namespaceActorId = ResolveNamespaceActorId(command.Owner);
        await EnsureTargetAsync<AgentProfileNamespaceGAgent>(namespaceActorId, ct);
        return await DispatchAsync(namespaceActorId, command.Operation, command, ct);
    }

    private static string ResolveProfileActorId(string profileId) =>
        AgentProfileActorIds.Profile(profileId);

    private static string ResolveProfileActorId(AgentProfileIdentity? identity, string profileActorId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var expectedActorId = ResolveProfileActorId(identity.ProfileId);
        EnsureExpectedActorId(profileActorId, expectedActorId, "profileActorId");
        return expectedActorId;
    }

    private static string ResolveNamespaceActorId(AgentProfileOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return AgentProfileActorIds.Namespace(owner);
    }

    private static void EnsureExpectedActorId(string actorId, string expectedActorId, string parameterName)
    {
        if (!string.Equals(actorId, expectedActorId, StringComparison.Ordinal))
            throw new ArgumentException("The actor target does not match the typed identity.", parameterName);
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
