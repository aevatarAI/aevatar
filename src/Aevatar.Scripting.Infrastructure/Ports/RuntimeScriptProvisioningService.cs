using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

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
        var result = await _dispatchService.DispatchAsync(
            new ProvisionScriptRuntimeCommand(
                definitionActorId,
                resolvedRevision,
                runtimeActorId,
                definitionSnapshot,
                scopeId),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script runtime provisioning dispatch failed.");

        var receipt = result.Receipt
            ?? throw new InvalidOperationException("Script runtime provisioning did not produce a receipt.");
        return receipt.ActorId;
    }
}
