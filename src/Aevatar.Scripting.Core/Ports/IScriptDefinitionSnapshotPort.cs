using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Core.Ports;

public interface IScriptDefinitionSnapshotPort
{
    async Task<ScriptDefinitionSnapshot?> TryGetAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        try
        {
            return await GetRequiredAsync(definitionActorId, requestedRevision, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct);
}
