namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptCatalogEntrySnapshot(
    string ScriptId,
    string ActiveRevision,
    string ActiveDefinitionActorId,
    string ActiveSourceHash,
    string PreviousRevision,
    IReadOnlyList<string> RevisionHistory,
    string LastProposalId);

public interface IScriptCatalogPort
{
    Task PromoteAsync(
        string catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct);

    Task RollbackAsync(
        string catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        CancellationToken ct);

    Task<ScriptCatalogEntrySnapshot?> GetEntryAsync(
        string catalogActorId,
        string scriptId,
        CancellationToken ct);
}
