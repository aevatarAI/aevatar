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
                requestedEventType ?? string.Empty),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime dispatch failed.");
    }
}
