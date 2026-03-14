using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogCommandService
    : IScriptCatalogCommandPort
{
    private readonly ICommandDispatchService<PromoteScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _promoteDispatchService;
    private readonly ICommandDispatchService<RollbackScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _rollbackDispatchService;

    public RuntimeScriptCatalogCommandService(
        ICommandDispatchService<PromoteScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> promoteDispatchService,
        ICommandDispatchService<RollbackScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> rollbackDispatchService)
    {
        _promoteDispatchService = promoteDispatchService ?? throw new ArgumentNullException(nameof(promoteDispatchService));
        _rollbackDispatchService = rollbackDispatchService ?? throw new ArgumentNullException(nameof(rollbackDispatchService));
    }

    public async Task PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct)
    {
        var result = await _promoteDispatchService.DispatchAsync(
            new PromoteScriptCatalogRevisionCommand(
                catalogActorId,
                scriptId,
                expectedBaseRevision,
                revision,
                definitionActorId,
                sourceHash,
                proposalId),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script catalog promotion dispatch failed.");
    }

    public async Task RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        string expectedCurrentRevision,
        CancellationToken ct)
    {
        var result = await _rollbackDispatchService.DispatchAsync(
            new RollbackScriptCatalogRevisionCommand(
                catalogActorId,
                scriptId,
                targetRevision,
                reason,
                proposalId,
                expectedCurrentRevision),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script catalog rollback dispatch failed.");
    }
}
