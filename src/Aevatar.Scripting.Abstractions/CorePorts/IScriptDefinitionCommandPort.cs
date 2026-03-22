namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptDefinitionUpsertResult(
    string ActorId,
    ScriptDefinitionSnapshot Snapshot);

public interface IScriptDefinitionCommandPort
{
    Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct);

    Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        string? scopeId,
        CancellationToken ct) =>
        UpsertDefinitionWithSnapshotAsync(
            scriptId,
            scriptRevision,
            sourceText,
            sourceHash,
            definitionActorId,
            ct);

    async Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct) =>
        (await UpsertDefinitionWithSnapshotAsync(
            scriptId,
            scriptRevision,
            sourceText,
            sourceHash,
            definitionActorId,
            ct)).ActorId;

    async Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        string? scopeId,
        CancellationToken ct) =>
        (await UpsertDefinitionWithSnapshotAsync(
            scriptId,
            scriptRevision,
            sourceText,
            sourceHash,
            definitionActorId,
            scopeId,
            ct)).ActorId;
}
