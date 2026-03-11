using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptExecutionLifecycleService
    : ScriptActorCommandPortBase<ScriptRuntimeGAgent>,
      IScriptRuntimeCommandPort
{
    private readonly RunScriptActorRequestAdapter _runScriptAdapter = new();

    public RuntimeScriptExecutionLifecycleService(
        IActorDispatchPort dispatchPort,
        RuntimeScriptActorAccessor actorAccessor)
        : base(dispatchPort, actorAccessor)
    {
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

        if (await ExistsAsync(actorId))
            return actorId;

        _ = await CreateActorAsync(actorId, ct);
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

        var runtimeActor = await GetRequiredActorAsync(runtimeActorId, "Script runtime actor not found");

        await DispatchAsync(
            runtimeActor.Id,
            new RunScriptActorRequest(
                RunId: runId,
                InputPayload: inputPayload?.Clone(),
                ScriptRevision: scriptRevision ?? string.Empty,
                DefinitionActorId: definitionActorId ?? string.Empty,
                RequestedEventType: requestedEventType ?? string.Empty),
            _runScriptAdapter.Map,
            ct);
    }
}
