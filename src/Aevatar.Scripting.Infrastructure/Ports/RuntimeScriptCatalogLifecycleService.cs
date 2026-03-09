using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogLifecycleService
{
    private readonly IActorDispatchPort _dispatchPort;
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RuntimeScriptQueryClient _queryClient;
    private readonly IScriptingActorAddressResolver _addressResolver;
    private readonly TimeSpan _catalogQueryTimeout;
    private readonly PromoteScriptRevisionActorRequestAdapter _promoteRevisionAdapter = new();
    private readonly RollbackScriptRevisionActorRequestAdapter _rollbackRevisionAdapter = new();
    private readonly QueryScriptCatalogEntryRequestAdapter _queryCatalogEntryAdapter = new();

    public RuntimeScriptCatalogLifecycleService(
        IActorDispatchPort dispatchPort,
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        IScriptingActorAddressResolver addressResolver,
        IScriptingPortTimeouts timeouts)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
        _catalogQueryTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetCatalogEntryQueryTimeout();
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
        var (resolvedCatalogActorId, _) = await ResolveAndGetOrCreateCatalogActorAsync(catalogActorId, ct);

        await _dispatchPort.DispatchAsync(
            resolvedCatalogActorId,
            _promoteRevisionAdapter.Map(
                new PromoteScriptRevisionActorRequest(
                    ScriptId: scriptId ?? string.Empty,
                    Revision: revision ?? string.Empty,
                    DefinitionActorId: definitionActorId ?? string.Empty,
                    SourceHash: sourceHash ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty,
                    ExpectedBaseRevision: expectedBaseRevision ?? string.Empty),
                resolvedCatalogActorId),
            ct);
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
        var (resolvedCatalogActorId, _) = await ResolveAndGetOrCreateCatalogActorAsync(catalogActorId, ct);

        await _dispatchPort.DispatchAsync(
            resolvedCatalogActorId,
            _rollbackRevisionAdapter.Map(
                new RollbackScriptRevisionActorRequest(
                    ScriptId: scriptId ?? string.Empty,
                    TargetRevision: targetRevision ?? string.Empty,
                    Reason: reason ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty,
                    ExpectedCurrentRevision: expectedCurrentRevision ?? string.Empty),
                resolvedCatalogActorId),
            ct);
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

        var response = await _queryClient.QueryActorAsync<ScriptCatalogEntryRespondedEvent>(
            actor,
            ScriptingQueryRouteConventions.CatalogReplyStreamPrefix,
            _catalogQueryTimeout,
            (requestId, replyStreamId) => _queryCatalogEntryAdapter.Map(resolvedCatalogActorId, requestId, replyStreamId, scriptId),
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

}
