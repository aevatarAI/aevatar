using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Ports;

public interface IScriptRuntimeCommandPort
{
    Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct);

    Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        string? scopeId,
        CancellationToken ct) =>
        RunRuntimeAsync(
            runtimeActorId,
            runId,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            ct);
}
