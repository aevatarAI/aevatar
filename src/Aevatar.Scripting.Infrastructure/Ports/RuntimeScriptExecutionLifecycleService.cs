using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptExecutionLifecycleService
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RunScriptActorRequestAdapter _runScriptAdapter = new();

    public RuntimeScriptExecutionLifecycleService(RuntimeScriptActorAccessor actorAccessor)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
    }

    public async Task<string> SpawnRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        var normalizedRevision = string.IsNullOrWhiteSpace(scriptRevision)
            ? "latest"
            : scriptRevision;
        var actorId = string.IsNullOrWhiteSpace(runtimeActorId)
            ? $"script-runtime:{definitionActorId}:{normalizedRevision}:{Guid.NewGuid():N}"
            : runtimeActorId;

        if (await _actorAccessor.ExistsAsync(actorId))
            return actorId;

        _ = await _actorAccessor.CreateAsync<ScriptRuntimeGAgent>(actorId, ct);
        return actorId;
    }

    public async Task RunRuntimeAsync(
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

        var runtimeActor = await _actorAccessor.GetAsync(runtimeActorId)
            ?? throw new InvalidOperationException($"Script runtime actor not found: {runtimeActorId}");

        await runtimeActor.HandleEventAsync(
            _runScriptAdapter.Map(
                new RunScriptActorRequest(
                    RunId: runId,
                    InputPayload: inputPayload?.Clone(),
                    ScriptRevision: scriptRevision ?? string.Empty,
                    DefinitionActorId: definitionActorId ?? string.Empty,
                    RequestedEventType: requestedEventType ?? string.Empty),
                runtimeActorId),
            ct);
    }
}
