using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public interface IScriptBehaviorRuntimeCapabilities
{
    Task<string> AskAIAsync(string prompt, CancellationToken ct);

    Task PublishAsync(IMessage eventPayload, TopologyAudience direction, CancellationToken ct);

    Task SendToAsync(string targetActorId, IMessage eventPayload, CancellationToken ct);

    Task PublishToSelfAsync(IMessage eventPayload, CancellationToken ct);

    Task<RuntimeCallbackLease> ScheduleSelfDurableSignalAsync(
        string callbackId,
        TimeSpan dueTime,
        IMessage eventPayload,
        CancellationToken ct);

    Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct);

    Task<string> CreateAgentAsync(string agentTypeAssemblyQualifiedName, string? actorId, CancellationToken ct);

    Task DestroyAgentAsync(string actorId, CancellationToken ct);

    Task LinkAgentsAsync(string parentActorId, string childActorId, CancellationToken ct);

    Task UnlinkAgentAsync(string childActorId, CancellationToken ct);

    Task<ScriptPromotionDecision> ProposeScriptEvolutionAsync(ScriptEvolutionProposal proposal, CancellationToken ct);

    Task<string> UpsertScriptDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct);

    Task<string> SpawnScriptRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct);

    Task RunScriptInstanceAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct);

    Task PromoteRevisionAsync(
        string catalogActorId,
        string scriptId,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct);

    Task RollbackRevisionAsync(
        string catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        CancellationToken ct);
}
