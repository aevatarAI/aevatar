using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Runtime;

public sealed class ScriptRuntimeCapabilities : IScriptRuntimeCapabilities
{
    private readonly IScriptInteractionCapabilities _interaction;
    private readonly IScriptAgentLifecycleCapabilities _agentLifecycle;
    private readonly IScriptEvolutionCapabilities _evolution;

    public ScriptRuntimeCapabilities(
        IScriptInteractionCapabilities interaction,
        IScriptAgentLifecycleCapabilities agentLifecycle,
        IScriptEvolutionCapabilities evolution)
    {
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _agentLifecycle = agentLifecycle ?? throw new ArgumentNullException(nameof(agentLifecycle));
        _evolution = evolution ?? throw new ArgumentNullException(nameof(evolution));
    }

    public Task<string> AskAIAsync(string prompt, CancellationToken ct) =>
        _interaction.AskAIAsync(prompt, ct);

    public Task PublishAsync(IMessage eventPayload, Aevatar.Foundation.Abstractions.TopologyAudience direction, CancellationToken ct) =>
        _interaction.PublishAsync(eventPayload, direction, ct);

    public Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct) =>
        _interaction.SendToAsync(targetActorId, eventPayload, ct);

    public Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct) =>
        _agentLifecycle.CreateAgentAsync(agentTypeAssemblyQualifiedName, actorId, ct);

    public Task DestroyAgentAsync(string actorId, CancellationToken ct) =>
        _agentLifecycle.DestroyAgentAsync(actorId, ct);

    public Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct) =>
        _agentLifecycle.LinkAgentsAsync(parentActorId, childActorId, ct);

    public Task UnlinkAgentAsync(string childActorId, CancellationToken ct) =>
        _agentLifecycle.UnlinkAgentAsync(childActorId, ct);

    public Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(ScriptEvolutionProposal proposal, CancellationToken ct) =>
        _evolution.ProposeScriptEvolutionAsync(proposal, ct);

    public Task<string> UpsertScriptDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct) =>
        _evolution.UpsertScriptDefinitionAsync(scriptId, scriptRevision, sourceText, sourceHash, definitionActorId, ct);

    public Task<string> SpawnScriptRuntimeAsync(string definitionActorId, string scriptRevision, string? runtimeActorId, CancellationToken ct) =>
        _evolution.SpawnScriptRuntimeAsync(definitionActorId, scriptRevision, runtimeActorId, ct);

    public Task RunScriptInstanceAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct) =>
        _evolution.RunScriptInstanceAsync(runtimeActorId, runId, inputPayload, scriptRevision, definitionActorId, requestedEventType, ct);

    public Task PromoteRevisionAsync(
        string catalogActorId,
        string scriptId,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct) =>
        _evolution.PromoteRevisionAsync(catalogActorId, scriptId, revision, definitionActorId, sourceHash, proposalId, ct);

    public Task RollbackRevisionAsync(
        string catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        CancellationToken ct) =>
        _evolution.RollbackRevisionAsync(catalogActorId, scriptId, targetRevision, reason, proposalId, ct);
}
