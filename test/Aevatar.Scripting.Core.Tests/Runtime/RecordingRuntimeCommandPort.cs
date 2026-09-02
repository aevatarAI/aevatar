using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

internal sealed class RecordingRuntimeCommandPort : IScriptRuntimeCommandPort
{
    public string? CompletionNotificationDeliveryId { get; private set; }

    public long CompletionNotificationExpiresAtUnixMs { get; private set; }

    public Task RunRuntimeAsync(
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
        CancellationToken ct)
    {
        _ = runtimeActorId;
        _ = runId;
        _ = inputPayload;
        _ = scriptRevision;
        _ = definitionActorId;
        _ = requestedEventType;
        _ = commandId;
        _ = correlationId;
        _ = scopeId;
        _ = completionNotificationActorId;
        CompletionNotificationDeliveryId = completionNotificationDeliveryId;
        CompletionNotificationExpiresAtUnixMs = completionNotificationExpiresAtUnixMs;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
