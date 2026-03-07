using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Abstractions.Definitions;

public interface IScriptEvolutionCapabilities
{
    Task<ScriptEvolutionDecision> ProposeScriptEvolutionAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct);

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

    Task<ScriptRuntimeRunAccepted> RunScriptInstanceAsync(
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
