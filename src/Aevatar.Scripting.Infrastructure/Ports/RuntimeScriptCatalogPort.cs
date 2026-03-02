using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogPort : IScriptCatalogPort
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly TimeSpan _queryTimeout;
    private readonly PromoteScriptRevisionActorRequestAdapter _promoteAdapter = new();
    private readonly RollbackScriptRevisionActorRequestAdapter _rollbackAdapter = new();
    private readonly QueryScriptCatalogEntryRequestAdapter _queryAdapter = new();

    public RuntimeScriptCatalogPort(
        IActorRuntime runtime,
        IStreamProvider streams,
        IScriptingPortTimeouts timeouts)
    {
        _runtime = runtime;
        _streams = streams;
        _queryTimeout = NormalizeTimeout(timeouts.CatalogEntryQueryTimeout);
    }

    public async Task PromoteAsync(
        string catalogActorId,
        string scriptId,
        string expectedBaseRevision,
        string revision,
        string definitionActorId,
        string sourceHash,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogActorId);

        var actor = await GetOrCreateCatalogActorAsync(catalogActorId, ct);
        await actor.HandleEventAsync(
            _promoteAdapter.Map(
                new PromoteScriptRevisionActorRequest(
                    ScriptId: scriptId ?? string.Empty,
                    Revision: revision ?? string.Empty,
                    DefinitionActorId: definitionActorId ?? string.Empty,
                    SourceHash: sourceHash ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty,
                    ExpectedBaseRevision: expectedBaseRevision ?? string.Empty),
                catalogActorId),
            ct);
    }

    public async Task RollbackAsync(
        string catalogActorId,
        string scriptId,
        string targetRevision,
        string reason,
        string proposalId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogActorId);

        var actor = await GetOrCreateCatalogActorAsync(catalogActorId, ct);
        await actor.HandleEventAsync(
            _rollbackAdapter.Map(
                new RollbackScriptRevisionActorRequest(
                    ScriptId: scriptId ?? string.Empty,
                    TargetRevision: targetRevision ?? string.Empty,
                    Reason: reason ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty),
                catalogActorId),
            ct);
    }

    public async Task<ScriptCatalogEntrySnapshot?> GetEntryAsync(
        string catalogActorId,
        string scriptId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(catalogActorId) || string.IsNullOrWhiteSpace(scriptId))
            return null;

        var actor = await _runtime.GetAsync(catalogActorId);
        if (actor == null)
            return null;

        var response = await ScriptQueryReplyAwaiter.QueryAsync<ScriptCatalogEntryRespondedEvent>(
            _streams,
            "scripting.query.catalog.reply",
            _queryTimeout,
            (requestId, replyStreamId) => actor.HandleEventAsync(
                _queryAdapter.Map(catalogActorId, requestId, replyStreamId, scriptId),
                ct),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            static requestId => $"Timeout waiting for script catalog entry query response. request_id={requestId}",
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

    private async Task<IActor> GetOrCreateCatalogActorAsync(string catalogActorId, CancellationToken ct)
    {
        if (await _runtime.ExistsAsync(catalogActorId))
        {
            return await _runtime.GetAsync(catalogActorId)
                ?? throw new InvalidOperationException($"Script catalog actor not found: {catalogActorId}");
        }

        return await _runtime.CreateAsync<ScriptCatalogGAgent>(catalogActorId, ct);
    }

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(45);
}
