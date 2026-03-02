using Aevatar.Scripting.Abstractions.Definitions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptCatalogEntrySnapshot(
    string ScriptId,
    string ActiveRevision,
    string ActiveDefinitionActorId,
    string ActiveSourceHash,
    string PreviousRevision,
    IReadOnlyList<string> RevisionHistory,
    string LastProposalId);

public interface IScriptLifecyclePort
{
    Task<ScriptPromotionDecision> ProposeAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct);

    Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct);

    Task<string> SpawnRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct);

    Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct);

    Task PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct);

    Task RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        CancellationToken ct);

    Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        string? catalogActorId,
        string scriptId,
        CancellationToken ct);
}
