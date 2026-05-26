using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCommandService : IScriptRuntimeCommandPort
{
    private readonly ICommandDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;
    private readonly ICommandOutcomeDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptDomainFactCommitted>? _outcomeDispatchService;

    public RuntimeScriptCommandService(
        ICommandDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService)
        : this(dispatchService, outcomeDispatchService: null)
    {
    }

    public RuntimeScriptCommandService(
        ICommandDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService,
        ICommandOutcomeDispatchService<RunScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptDomainFactCommitted>? outcomeDispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _outcomeDispatchService = outcomeDispatchService;
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
        var command = new RunScriptRuntimeCommand(
            runtimeActorId,
            runId,
            inputPayload?.Clone(),
            scriptRevision ?? string.Empty,
            definitionActorId ?? string.Empty,
            requestedEventType ?? string.Empty,
            scopeId,
            string.IsNullOrWhiteSpace(commandId) ? null : commandId.Trim(),
            string.IsNullOrWhiteSpace(correlationId) ? null : correlationId.Trim());
        var result = _outcomeDispatchService == null
            ? await _dispatchService.DispatchAsync(command, ct)
            : await DispatchAndAwaitOutcomeAsync(command, ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime dispatch failed.");
    }

    private async Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>> DispatchAndAwaitOutcomeAsync(
        RunScriptRuntimeCommand command,
        CancellationToken ct)
    {
        var result = await _outcomeDispatchService!.DispatchAndAwaitOutcomeAsync(command, ct);
        if (!result.Succeeded)
            return CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Failure(result.Error);

        var receipt = result.Receipt
            ?? throw new InvalidOperationException("Script runtime dispatch did not produce a receipt.");
        return CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(receipt);
    }
}
