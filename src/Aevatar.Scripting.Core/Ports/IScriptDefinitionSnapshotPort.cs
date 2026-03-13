namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptDefinitionSnapshot(
    string ScriptId,
    string Revision,
    string SourceText,
    string SourceHash,
    string StateTypeUrl,
    string ReadModelTypeUrl,
    string ReadModelSchemaVersion,
    string ReadModelSchemaHash);

public interface IScriptDefinitionSnapshotPort
{
    Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct);
}
