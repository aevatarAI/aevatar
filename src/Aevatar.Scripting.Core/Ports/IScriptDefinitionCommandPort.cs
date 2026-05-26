using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Core.Ports;

public sealed record ScriptDefinitionUpsertResult(
    string ActorId,
    ScriptDefinitionSnapshot Snapshot,
    ScriptingCommandAcceptedReceipt AcceptedReceipt);

public interface IScriptDefinitionCommandPort
{
    Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        string scriptId,
        string scriptRevision,
        ScriptPackageSpec scriptPackage,
        string? definitionActorId,
        CancellationToken ct);

    Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        string scriptId,
        string scriptRevision,
        ScriptPackageSpec scriptPackage,
        string? definitionActorId,
        string? scopeId,
        CancellationToken ct) =>
        UpsertDefinitionWithSnapshotAsync(
            scriptId,
            scriptRevision,
            scriptPackage,
            definitionActorId,
            ct);

    async Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        ScriptPackageSpec scriptPackage,
        string? definitionActorId,
        CancellationToken ct) =>
        (await UpsertDefinitionWithSnapshotAsync(
            scriptId,
            scriptRevision,
            scriptPackage,
            definitionActorId,
            ct)).ActorId;

    async Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        ScriptPackageSpec scriptPackage,
        string? definitionActorId,
        string? scopeId,
        CancellationToken ct) =>
        (await UpsertDefinitionWithSnapshotAsync(
            scriptId,
            scriptRevision,
            scriptPackage,
            definitionActorId,
            scopeId,
            ct)).ActorId;
}
