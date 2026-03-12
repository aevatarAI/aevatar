using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionCommandService : IScriptDefinitionCommandPort
{
    private readonly ICommandDispatchService<UpsertScriptDefinitionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _dispatchService;

    public RuntimeScriptDefinitionCommandService(
        ICommandDispatchService<UpsertScriptDefinitionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> dispatchService)
    {
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
    }

    public async Task<string> UpsertDefinitionAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct)
    {
        var result = await _dispatchService.DispatchAsync(
            new UpsertScriptDefinitionCommand(
                scriptId,
                scriptRevision,
                sourceText,
                sourceHash,
                definitionActorId),
            ct);
        if (!result.Succeeded || result.Receipt == null)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script definition dispatch failed.");

        return result.Receipt.ActorId;
    }
}
