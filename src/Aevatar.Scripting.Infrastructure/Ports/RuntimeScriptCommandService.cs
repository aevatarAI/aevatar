using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

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
        CancellationToken ct)
    {
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
