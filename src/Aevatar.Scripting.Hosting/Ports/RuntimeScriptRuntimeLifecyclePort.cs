using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptRuntimeLifecyclePort : IScriptRuntimeLifecyclePort
{
    private readonly IActorRuntime _runtime;
    private readonly IScriptDefinitionSnapshotPort _snapshotPort;
    private readonly RunScriptCommandAdapter _runCommandAdapter = new();

    public RuntimeScriptRuntimeLifecyclePort(
        IActorRuntime runtime,
        IScriptDefinitionSnapshotPort snapshotPort)
    {
        _runtime = runtime;
        _snapshotPort = snapshotPort;
    }

    public async Task<string> SpawnAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct)
    {
        var snapshot = await _snapshotPort.GetRequiredAsync(definitionActorId, scriptRevision, ct);
        var actorId = string.IsNullOrWhiteSpace(runtimeActorId)
            ? $"script-runtime:{snapshot.ScriptId}:{Guid.NewGuid():N}"
            : runtimeActorId;

        if (await _runtime.ExistsAsync(actorId))
            return actorId;

        _ = await _runtime.CreateAsync<ScriptRuntimeGAgent>(actorId, ct);
        return actorId;
    }

    public async Task RunAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var runtimeActor = await _runtime.GetAsync(runtimeActorId)
            ?? throw new InvalidOperationException($"Script runtime actor not found: {runtimeActorId}");

        await runtimeActor.HandleEventAsync(
            _runCommandAdapter.Map(
                new RunScriptCommand(
                    RunId: runId,
                    InputPayload: inputPayload?.Clone(),
                    ScriptRevision: scriptRevision ?? string.Empty,
                    DefinitionActorId: definitionActorId ?? string.Empty,
                    RequestedEventType: requestedEventType ?? string.Empty),
                runtimeActorId),
            ct);
    }
}
