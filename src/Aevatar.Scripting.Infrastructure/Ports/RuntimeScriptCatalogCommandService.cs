using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogCommandService
    : IScriptCatalogCommandPort
{
    private readonly ICommandDispatchService<PromoteScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _promoteDispatchService;
    private readonly ICommandDispatchService<RollbackScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _rollbackDispatchService;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IScriptAuthorityReadModelActivationPort? _authorityReadModelActivationPort;

    public RuntimeScriptCatalogCommandService(
        ICommandDispatchService<PromoteScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> promoteDispatchService,
        ICommandDispatchService<RollbackScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> rollbackDispatchService,
        IScriptingActorAddressResolver addressResolver,
        RuntimeScriptActorAccessor actorAccessor,
        IScriptAuthorityReadModelActivationPort? authorityReadModelActivationPort = null)
    {
        _promoteDispatchService = promoteDispatchService ?? throw new ArgumentNullException(nameof(promoteDispatchService));
        _rollbackDispatchService = rollbackDispatchService ?? throw new ArgumentNullException(nameof(rollbackDispatchService));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _authorityReadModelActivationPort = authorityReadModelActivationPort;
    }

    public async Task<ScriptingCommandAcceptedReceipt> PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct) =>
        await PromoteCatalogRevisionAsync(
            catalogActorId,
            scriptId,
            expectedBaseRevision,
            revision,
            definitionActorId,
            sourceHash,
            proposalId,
            scopeId: null,
            ct);

    public async Task<ScriptingCommandAcceptedReceipt> PromoteCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        string? scopeId,
        CancellationToken ct)
    {
        var resolvedCatalogActorId = string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId(scopeId)
            : catalogActorId;
        _ = await _actorAccessor.GetOrCreateAsync<ScriptCatalogGAgent>(
            resolvedCatalogActorId,
            "Script catalog actor not found",
            ct);
        if (_authorityReadModelActivationPort != null)
            await _authorityReadModelActivationPort.ActivateAsync(resolvedCatalogActorId, ct);

        var result = await _promoteDispatchService.DispatchAsync(
            new PromoteScriptCatalogRevisionCommand(
                resolvedCatalogActorId,
                scriptId,
                expectedBaseRevision,
                revision,
                definitionActorId,
                sourceHash,
                proposalId,
                scopeId),
            ct);
        if (!result.Succeeded || result.Receipt == null)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script catalog promotion dispatch failed.");
        return result.Receipt;
    }

    public async Task<ScriptingCommandAcceptedReceipt> RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        string expectedCurrentRevision,
        CancellationToken ct) =>
        await RollbackCatalogRevisionAsync(
            catalogActorId,
            scriptId,
            targetRevision,
            reason,
            proposalId,
            expectedCurrentRevision,
            scopeId: null,
            ct);

    public async Task<ScriptingCommandAcceptedReceipt> RollbackCatalogRevisionAsync(
        string? catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        string expectedCurrentRevision,
        string? scopeId,
        CancellationToken ct)
    {
        var resolvedCatalogActorId = string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId(scopeId)
            : catalogActorId;
        _ = await _actorAccessor.GetOrCreateAsync<ScriptCatalogGAgent>(
            resolvedCatalogActorId,
            "Script catalog actor not found",
            ct);
        if (_authorityReadModelActivationPort != null)
            await _authorityReadModelActivationPort.ActivateAsync(resolvedCatalogActorId, ct);

        var result = await _rollbackDispatchService.DispatchAsync(
            new RollbackScriptCatalogRevisionCommand(
                resolvedCatalogActorId,
                scriptId,
                targetRevision,
                reason,
                proposalId,
                expectedCurrentRevision,
                scopeId),
            ct);
        if (!result.Succeeded || result.Receipt == null)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script catalog rollback dispatch failed.");
        return result.Receipt;
    }
}
