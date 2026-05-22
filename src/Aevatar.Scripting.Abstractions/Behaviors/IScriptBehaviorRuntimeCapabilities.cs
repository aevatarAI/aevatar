using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions.Behaviors;

// Refactor (iter27/cluster-029-scripting-runtime-raw-actor-lifecycle):
//   Old pattern: Scripting behavior runtime exposes raw IActorRuntime lifecycle/topology by assembly-qualified type name and caller-supplied actor ids
//   New principle: Delete raw script-facing actor lifecycle/topology API; keep existing typed scripting ports (provisioning/command/definition/catalog/evolution)
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
