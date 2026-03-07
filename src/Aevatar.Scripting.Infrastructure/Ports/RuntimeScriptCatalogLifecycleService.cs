using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogLifecycleService
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RuntimeScriptQueryClient _queryClient;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _catalogQueryTimeout;
    private readonly TimeSpan _catalogMutationTimeout;
    private readonly PromoteScriptRevisionActorRequestAdapter _promoteRevisionAdapter = new();
    private readonly RollbackScriptRevisionActorRequestAdapter _rollbackRevisionAdapter = new();
    private readonly QueryScriptCatalogEntryRequestAdapter _queryCatalogEntryAdapter = new();

    public RuntimeScriptCatalogLifecycleService(
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _catalogQueryTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetCatalogEntryQueryTimeout();
        _catalogMutationTimeout = timeouts.GetCatalogMutationTimeout();
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
        var (resolvedCatalogActorId, actor) = await ResolveAndGetOrCreateCatalogActorAsync(catalogActorId, ct);

        var response = await _queryClient.QueryAsync<ScriptCatalogCommandRespondedEvent>(
            ScriptingQueryRouteConventions.CatalogReplyStreamPrefix,
            _catalogMutationTimeout,
            (requestId, replyStreamId) => actor.HandleEventAsync(
                _promoteRevisionAdapter.Map(
                    new PromoteScriptRevisionActorRequest(
                        ScriptId: scriptId ?? string.Empty,
                        Revision: revision ?? string.Empty,
                        DefinitionActorId: definitionActorId ?? string.Empty,
                        SourceHash: sourceHash ?? string.Empty,
                        ProposalId: proposalId ?? string.Empty,
                        ExpectedBaseRevision: expectedBaseRevision ?? string.Empty,
                        RequestId: requestId,
                        ReplyStreamId: replyStreamId),
                    resolvedCatalogActorId),
                ct),
            static (response, requestId) => string.Equals(response.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildCatalogMutationTimeoutMessage,
            ct);
        if (!response.Succeeded ||
            !string.Equals(response.ActiveRevision, revision, StringComparison.Ordinal) ||
            !string.Equals(response.ActiveDefinitionActorId, definitionActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.FailureReason)
                    ? $"Catalog promotion did not converge. script_id=`{scriptId}` expected_revision=`{revision}` expected_definition_actor_id=`{definitionActorId}` actual_revision=`{response.ActiveRevision ?? string.Empty}` actual_definition_actor_id=`{response.ActiveDefinitionActorId ?? string.Empty}`."
                    : response.FailureReason);
        }
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
        var (resolvedCatalogActorId, actor) = await ResolveAndGetOrCreateCatalogActorAsync(catalogActorId, ct);

        var response = await _queryClient.QueryAsync<ScriptCatalogCommandRespondedEvent>(
            ScriptingQueryRouteConventions.CatalogReplyStreamPrefix,
            _catalogMutationTimeout,
            (requestId, replyStreamId) => actor.HandleEventAsync(
                _rollbackRevisionAdapter.Map(
                    new RollbackScriptRevisionActorRequest(
                        ScriptId: scriptId ?? string.Empty,
                        TargetRevision: targetRevision ?? string.Empty,
                        Reason: reason ?? string.Empty,
                        ProposalId: proposalId ?? string.Empty,
                        ExpectedCurrentRevision: expectedCurrentRevision ?? string.Empty,
                        RequestId: requestId,
                        ReplyStreamId: replyStreamId),
                    resolvedCatalogActorId),
                ct),
            static (response, requestId) => string.Equals(response.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildCatalogMutationTimeoutMessage,
            ct);
        if (!response.Succeeded || !string.Equals(response.ActiveRevision, targetRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.FailureReason)
                    ? $"Catalog rollback did not converge. script_id=`{scriptId}` expected_revision=`{targetRevision}` actual_revision=`{response.ActiveRevision ?? string.Empty}`."
                    : response.FailureReason);
        }
    }

    public async Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        string? catalogActorId,
        string scriptId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
            return null;

        var resolvedCatalogActorId = ResolveCatalogActorId(catalogActorId);
        var actor = await _actorAccessor.GetAsync(resolvedCatalogActorId);
        if (actor == null)
            return null;

        return await QueryCatalogEntryAsync(resolvedCatalogActorId, actor, scriptId, ct);
    }

    private string ResolveCatalogActorId(string? catalogActorId) =>
        string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : catalogActorId;

    private async Task<(string ActorId, IActor Actor)> ResolveAndGetOrCreateCatalogActorAsync(
        string? catalogActorId,
        CancellationToken ct)
    {
        var resolvedCatalogActorId = ResolveCatalogActorId(catalogActorId);
        var actor = await _actorAccessor.GetOrCreateAsync<ScriptCatalogGAgent>(
            resolvedCatalogActorId,
            "Script catalog actor not found",
            ct);
        return (resolvedCatalogActorId, actor);
    }

    private async Task<ScriptCatalogEntrySnapshot?> QueryCatalogEntryAsync(
        string catalogActorId,
        IActor actor,
        string scriptId,
        CancellationToken ct)
    {
        var response = await _queryClient.QueryActorAsync<ScriptCatalogEntryRespondedEvent>(
            actor,
            ScriptingQueryRouteConventions.CatalogReplyStreamPrefix,
            _catalogQueryTimeout,
            (requestId, replyStreamId) => _queryCatalogEntryAdapter.Map(catalogActorId, requestId, replyStreamId, scriptId),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildCatalogEntryTimeoutMessage,
            ct);
        if (!response.Found)
            return null;

        return new ScriptCatalogEntrySnapshot(
            ScriptId: response.ScriptId ?? string.Empty,
            ActiveRevision: response.ActiveRevision ?? string.Empty,
            ActiveDefinitionActorId: response.ActiveDefinitionActorId ?? string.Empty,
            ActiveSourceHash: response.ActiveSourceHash ?? string.Empty,
            PreviousRevision: response.PreviousRevision ?? string.Empty,
            RevisionHistory: response.RevisionHistory.ToArray(),
            LastProposalId: response.LastProposalId ?? string.Empty);
    }

}
