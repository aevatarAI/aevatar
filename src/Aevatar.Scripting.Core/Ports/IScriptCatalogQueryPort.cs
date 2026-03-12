namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptCatalogEntrySnapshot(
    string ScriptId,
    string ActiveRevision,
    string ActiveDefinitionActorId,
    string ActiveSourceHash,
    string PreviousRevision,
    IReadOnlyList<string> RevisionHistory,
    string LastProposalId);

public interface IScriptCatalogQueryPort
{
    Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        string? catalogActorId,
        string scriptId,
        CancellationToken ct);
}
