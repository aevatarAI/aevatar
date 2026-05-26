using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptProvisioningService : IScriptRuntimeProvisioningPort
{
    private readonly ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;
    private readonly ICommandDispatchPipeline<ProvisionScriptRuntimeCommand, ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>? _dispatchPipeline;
    private readonly IActorHandledDispatchPort? _handledDispatchPort;

    public RuntimeScriptProvisioningService(
        ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService)
        : this(dispatchService, null, null)
    {
    }

    public RuntimeScriptProvisioningService(
        ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService,
        ICommandDispatchPipeline<ProvisionScriptRuntimeCommand, ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>? dispatchPipeline,
        IActorHandledDispatchPort? handledDispatchPort)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _dispatchPipeline = dispatchPipeline;
        _handledDispatchPort = handledDispatchPort;
    }

    public async Task<string> EnsureRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        ScriptDefinitionSnapshot definitionSnapshot,
        CancellationToken ct) =>
        await EnsureRuntimeAsync(
            definitionActorId,
            scriptRevision,
            runtimeActorId,
            definitionSnapshot,
            scopeId: null,
            ct);

    public async Task<string> EnsureRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        ScriptDefinitionSnapshot definitionSnapshot,
        string? scopeId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);
        ArgumentNullException.ThrowIfNull(definitionSnapshot);

        if (!string.IsNullOrWhiteSpace(scriptRevision) &&
            !string.Equals(scriptRevision, definitionSnapshot.Revision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Script runtime provisioning requires a definition snapshot for revision `{scriptRevision}`, but received `{definitionSnapshot.Revision}`.");
        }

        var resolvedRevision = string.IsNullOrWhiteSpace(scriptRevision)
            ? definitionSnapshot.Revision
            : scriptRevision;
        var command = new ProvisionScriptRuntimeCommand(
            definitionActorId,
            resolvedRevision,
            runtimeActorId,
            definitionSnapshot,
            scopeId);
        if (_dispatchPipeline != null && _handledDispatchPort != null)
        {
            var prepared = await _dispatchPipeline.PrepareAsync(command, ct);
            if (!prepared.Succeeded || prepared.Target == null)
                throw prepared.Error?.ToException() ?? new InvalidOperationException("Script runtime provisioning dispatch failed.");

            await _handledDispatchPort.DispatchAndWaitHandledAsync(
                prepared.Target.Target.TargetId,
                prepared.Target.Envelope,
                ct);
            return prepared.Target.Receipt.ActorId;
        }

        var result = await _dispatchService.DispatchAsync(command, ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime provisioning dispatch failed.");

        var receipt = result.Receipt
            ?? throw new InvalidOperationException("Script runtime provisioning did not produce a receipt.");
        return receipt.ActorId;
    }
}
