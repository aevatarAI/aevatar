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

    // Refactor (iter25/cluster-026-scope-service-script-stream-inline-orchestration):
    //   Old pattern: script runtime dispatch derived command and correlation ids from the run id
    //   New principle: callers can pass explicit command and correlation ids while keeping run identity separate
    Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        string commandId,
        string correlationId,
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
            scopeId,
            ct);
}
