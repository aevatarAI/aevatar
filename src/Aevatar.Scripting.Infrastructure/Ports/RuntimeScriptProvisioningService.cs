using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

// Refactor (iter111/cluster-111-handled-dispatch-contract):
//   Old pattern: Public CQRS/runtime surface exposes IActorHandledDispatchPort, lets command paths synchronously wait for one actor turn, then returns DispatchAdmission.
//   New principle: Command skeleton depends only on accepted inbox dispatch; any handled/committed/readmodel stage is modeled as explicit follow-up observation or continuation event, never as dispatch ACK.
public sealed class RuntimeScriptProvisioningService : IScriptRuntimeProvisioningPort
{
    private readonly ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;

    public RuntimeScriptProvisioningService(
        ICommandDispatchService<ProvisionScriptRuntimeCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
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
        var result = await _dispatchService.DispatchAsync(command, ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime provisioning dispatch failed.");

        var receipt = result.Receipt
            ?? throw new InvalidOperationException("Script runtime provisioning did not produce a receipt.");
        return receipt.ActorId;
    }
}
