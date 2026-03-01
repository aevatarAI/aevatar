using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
{
    private readonly IActorRuntime _runtime;

    public RuntimeScriptDefinitionSnapshotPort(IActorRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        var actor = await _runtime.GetAsync(definitionActorId)
            ?? throw new InvalidOperationException($"Script definition actor not found: {definitionActorId}");
        if (actor.Agent is not IScriptDefinitionSnapshotSource source)
            throw new InvalidOperationException(
                $"Actor `{definitionActorId}` does not implement IScriptDefinitionSnapshotSource.");

        var snapshot = source.GetSnapshot();
        if (string.IsNullOrWhiteSpace(snapshot.SourceText))
            throw new InvalidOperationException(
                $"Script definition source_text is empty for actor `{definitionActorId}`.");
        if (!string.IsNullOrWhiteSpace(requestedRevision) &&
            !string.Equals(requestedRevision, snapshot.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Requested script revision `{requestedRevision}` does not match definition snapshot revision `{snapshot.Revision}`.");

        ct.ThrowIfCancellationRequested();
        return snapshot;
    }
}
