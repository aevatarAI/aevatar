using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptProvisioningService : IScriptRuntimeProvisioningPort
{
    private readonly ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;
    private readonly ICommandOutcomeDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptBehaviorBoundEvent>? _outcomeDispatchService;

    public RuntimeScriptProvisioningService(
        ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService)
        : this(dispatchService, outcomeDispatchService: null)
    {
    }

    public RuntimeScriptProvisioningService(
        ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService,
        ICommandOutcomeDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError, ScriptBehaviorBoundEvent>? outcomeDispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _outcomeDispatchService = outcomeDispatchService;
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
        var result = _outcomeDispatchService == null
            ? await _dispatchService.DispatchAsync(command, ct)
            : await DispatchAndAwaitOutcomeAsync(command, ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime provisioning dispatch failed.");

        var receipt = result.Receipt
            ?? throw new InvalidOperationException("Script runtime provisioning did not produce a receipt.");
        return receipt.ActorId;
    }

    private async Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>> DispatchAndAwaitOutcomeAsync(
        ProvisionScriptRuntimeCommand command,
        CancellationToken ct)
    {
        var result = await _outcomeDispatchService!.DispatchAndAwaitOutcomeAsync(command, ct);
        if (!result.Succeeded)
            return CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Failure(result.Error);

        var receipt = result.Receipt
            ?? throw new InvalidOperationException("Script runtime provisioning did not produce a receipt.");
        return CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(receipt);
    }
}
