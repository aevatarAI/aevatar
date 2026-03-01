namespace Aevatar.Scripting.Core.Ports;

public interface IScriptDefinitionLifecyclePort
{
    Task<string> UpsertAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct);
}
