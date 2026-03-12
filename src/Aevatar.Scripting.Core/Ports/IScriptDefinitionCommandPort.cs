namespace Aevatar.Scripting.Core.Ports;

public interface IScriptDefinitionCommandPort
{
    Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct);
}
