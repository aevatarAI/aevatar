using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

// Refactor (iter149/issue1132): Old pattern: runtime script commands had an optional handled-dispatch bypass around the typed command service.  New principle: runtime script commands always use the typed accepted-only dispatch service.
public sealed class RuntimeScriptCommandService : IScriptRuntimeCommandPort
{
    private readonly ICommandDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;

    public RuntimeScriptCommandService(
        ICommandDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
    }

    public async Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        CancellationToken ct) =>
        await RunRuntimeAsync(
            runtimeActorId,
            runId,
            inputPayload,
            scriptRevision,
            definitionActorId,
            requestedEventType,
            scopeId: null,
            ct);

    public async Task RunRuntimeAsync(
        string runtimeActorId,
        string runId,
        Any? inputPayload,
        string scriptRevision,
        string definitionActorId,
        string requestedEventType,
        string? scopeId,
        CancellationToken ct) =>
        await RunRuntimeAsync(
            runtimeActorId,
            runId,
            commandId: string.Empty,
            correlationId: string.Empty,
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
    //   Old pattern: runtime command dispatch collapsed tracking identity onto the run id
    //   New principle: preserve distinct run, command, and correlation identities through the scripting dispatch port
    public async Task RunRuntimeAsync(
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
        await RunRuntimeAsync(
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

    public async Task RunRuntimeAsync(
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
        await RunRuntimeAsync(
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

    public async Task RunRuntimeAsync(
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
        var command = new RunScriptRuntimeCommand(
            runtimeActorId,
            runId,
            inputPayload?.Clone(),
            scriptRevision ?? string.Empty,
            definitionActorId ?? string.Empty,
            requestedEventType ?? string.Empty,
            scopeId,
            string.IsNullOrWhiteSpace(commandId) ? null : commandId.Trim(),
            string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim(),
            string.IsNullOrWhiteSpace(completionNotificationActorId)
                ? null
                : completionNotificationActorId.Trim(),
            string.IsNullOrWhiteSpace(completionNotificationDeliveryId)
                ? null
                : completionNotificationDeliveryId.Trim(),
            completionNotificationExpiresAtUnixMs);
        var result = await _dispatchService.DispatchAsync(command, ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime dispatch failed.");
    }
}
