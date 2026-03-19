using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogCommandService
    : IScriptCatalogCommandPort
{
    private static readonly TimeSpan CatalogObservationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CatalogObservationPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ICommandDispatchService<PromoteScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _promoteDispatchService;
    private readonly ICommandDispatchService<RollbackScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> _rollbackDispatchService;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly IScriptAuthorityReadModelActivationPort _authorityReadModelActivationPort;
    private readonly IScriptCatalogQueryPort _catalogQueryPort;

    public RuntimeScriptCatalogCommandService(
        ICommandDispatchService<PromoteScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> promoteDispatchService,
        ICommandDispatchService<RollbackScriptCatalogRevisionCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> rollbackDispatchService,
        IScriptingActorAddressResolver addressResolver,
        RuntimeScriptActorAccessor actorAccessor,
        IScriptAuthorityReadModelActivationPort authorityReadModelActivationPort,
        IScriptCatalogQueryPort catalogQueryPort)
    {
        _promoteDispatchService = promoteDispatchService ?? throw new ArgumentNullException(nameof(promoteDispatchService));
        _rollbackDispatchService = rollbackDispatchService ?? throw new ArgumentNullException(nameof(rollbackDispatchService));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _authorityReadModelActivationPort = authorityReadModelActivationPort ?? throw new ArgumentNullException(nameof(authorityReadModelActivationPort));
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
    }

    public async Task PromoteCatalogRevisionAsync(
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

    public async Task PromoteCatalogRevisionAsync(
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
                proposalId,
                scopeId),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script catalog promotion dispatch failed.");

        await WaitForCatalogEntryAsync(
            resolvedCatalogActorId,
            scriptId,
            static (entry, state) =>
                Matches(entry.ActiveRevision, state.Revision) &&
                Matches(entry.ActiveDefinitionActorId, state.DefinitionActorId) &&
                Matches(entry.ActiveSourceHash, state.SourceHash) &&
                Matches(entry.LastProposalId, state.ProposalId),
            new CatalogEntryExpectation(revision, definitionActorId, sourceHash, proposalId),
            "promotion",
            ct);
    }

    public async Task RollbackCatalogRevisionAsync(
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

    public async Task RollbackCatalogRevisionAsync(
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
                expectedCurrentRevision,
                scopeId),
            ct);
        if (!result.Succeeded)
            throw result.Error?.ToException() ?? new InvalidOperationException("Script catalog rollback dispatch failed.");

        await WaitForCatalogEntryAsync(
            resolvedCatalogActorId,
            scriptId,
            static (entry, state) =>
                Matches(entry.ActiveRevision, state.Revision) &&
                Matches(entry.LastProposalId, state.ProposalId),
            new CatalogEntryExpectation(targetRevision, string.Empty, string.Empty, proposalId),
            "rollback",
            ct);
    }

    private async Task WaitForCatalogEntryAsync(
        string catalogActorId,
        string scriptId,
        Func<ScriptCatalogEntrySnapshot, CatalogEntryExpectation, bool> predicate,
        CatalogEntryExpectation expectation,
        string operationName,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CatalogObservationTimeout);

        ScriptCatalogEntrySnapshot? last = null;
        try
        {
            while (true)
            {
                last = await _catalogQueryPort.GetCatalogEntryAsync(catalogActorId, scriptId, timeout.Token);
                if (last != null && predicate(last, expectation))
                    return;

                await Task.Delay(CatalogObservationPollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for script catalog {operationName} observation. catalog_actor_id={catalogActorId} script_id={scriptId} expected_revision={expectation.Revision} expected_proposal_id={expectation.ProposalId} last_active_revision={last?.ActiveRevision ?? string.Empty} last_proposal_id={last?.LastProposalId ?? string.Empty}");
        }
    }

    private static bool Matches(string actual, string expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(actual, expected, StringComparison.Ordinal);

    private sealed record CatalogEntryExpectation(
        string Revision,
        string DefinitionActorId,
        string SourceHash,
        string ProposalId);
}
