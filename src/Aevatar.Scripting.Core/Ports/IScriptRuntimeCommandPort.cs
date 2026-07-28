using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Ports;

public interface IScriptRuntimeCommandPort
{
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
        string? completionNotificationActorId,
        string? completionNotificationDeliveryId,
        long completionNotificationExpiresAtUnixMs,
        CancellationToken ct);

    Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct) =>
        RunRuntimeAsync(
            runtimeActorId,
            runId,
            string.Empty,
            string.Empty,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            scopeId: null,
            completionNotificationActorId: null,
            completionNotificationDeliveryId: null,
            completionNotificationExpiresAtUnixMs: 0,
            ct);

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
            string.Empty,
            string.Empty,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            scopeId,
            completionNotificationActorId: null,
            completionNotificationDeliveryId: null,
            completionNotificationExpiresAtUnixMs: 0,
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
            commandId,
            correlationId,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            scopeId,
            completionNotificationActorId: null,
            completionNotificationDeliveryId: null,
            completionNotificationExpiresAtUnixMs: 0,
            ct);

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
        string? completionNotificationActorId,
        CancellationToken ct) =>
        RunRuntimeAsync(
            runtimeActorId,
            runId,
            commandId,
            correlationId,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            scopeId,
            completionNotificationActorId,
            completionNotificationDeliveryId: null,
            completionNotificationExpiresAtUnixMs: 0,
            ct);
}
