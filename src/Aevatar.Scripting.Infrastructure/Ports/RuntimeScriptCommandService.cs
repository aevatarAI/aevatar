using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCommandService : IScriptRuntimeCommandPort
{
    private readonly ICommandDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;
    private readonly IScriptExecutionReadModelActivationPort _readModelActivationPort;

    public RuntimeScriptCommandService(
        ICommandDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService,
        IScriptExecutionReadModelActivationPort readModelActivationPort)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _readModelActivationPort = readModelActivationPort ?? throw new ArgumentNullException(nameof(readModelActivationPort));
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
        CancellationToken ct)
    {
        _ = await _readModelActivationPort.ActivateAsync(runtimeActorId, ct);

        var result = await _dispatchService.DispatchAsync(
            new RunScriptRuntimeCommand(
                runtimeActorId,
                runId,
                inputPayload?.Clone(),
                scriptRevision ?? string.Empty,
                definitionActorId ?? string.Empty,
                requestedEventType ?? string.Empty,
                scopeId,
                string.IsNullOrWhiteSpace(commandId) ? null : commandId.Trim(),
                string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim()),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime dispatch failed.");
    }
}
