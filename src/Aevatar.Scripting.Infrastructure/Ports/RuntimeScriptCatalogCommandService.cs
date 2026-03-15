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
    private readonly IScriptAuthorityReadModelActivationPort _authorityReadModelActivationPort;

    public RuntimeScriptCatalogCommandService(
        ICommandDispatchService<PromoteScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> promoteDispatchService,
        ICommandDispatchService<RollbackScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> rollbackDispatchService,
        IScriptingActorAddressResolver addressResolver,
        RuntimeScriptActorAccessor actorAccessor,
        IScriptAuthorityReadModelActivationPort authorityReadModelActivationPort)
    {
        _promoteDispatchService = promoteDispatchService ?? throw new ArgumentNullException(nameof(promoteDispatchService));
        _rollbackDispatchService = rollbackDispatchService ?? throw new ArgumentNullException(nameof(rollbackDispatchService));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _authorityReadModelActivationPort = authorityReadModelActivationPort ?? throw new ArgumentNullException(nameof(authorityReadModelActivationPort));
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
        var resolvedCatalogActorId = string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : catalogActorId;
        _ = await _actorAccessor.GetOrCreateAsync<ScriptCatalogGAgent>(
            resolvedCatalogActorId,
            "Script catalog actor not found",
            ct);
        await _authorityReadModelActivationPort.ActivateAsync(resolvedCatalogActorId, ct);

        var result = await _promoteDispatchService.DispatchAsync(
            new PromoteScriptCatalogRevisionCommand(
                resolvedCatalogActorId,
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
        var resolvedCatalogActorId = string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : catalogActorId;
        _ = await _actorAccessor.GetOrCreateAsync<ScriptCatalogGAgent>(
            resolvedCatalogActorId,
            "Script catalog actor not found",
            ct);
        await _authorityReadModelActivationPort.ActivateAsync(resolvedCatalogActorId, ct);

        var result = await _rollbackDispatchService.DispatchAsync(
            new RollbackScriptCatalogRevisionCommand(
                resolvedCatalogActorId,
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
