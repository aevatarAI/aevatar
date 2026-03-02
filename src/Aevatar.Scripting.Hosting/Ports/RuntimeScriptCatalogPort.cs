using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptCatalogPort : IScriptCatalogPort
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(45);

    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly PromoteScriptRevisionCommandAdapter _promoteAdapter = new();
    private readonly RollbackScriptRevisionCommandAdapter _rollbackAdapter = new();
    private readonly QueryScriptCatalogEntryRequestAdapter _queryAdapter = new();

    public RuntimeScriptCatalogPort(
        IActorRuntime runtime,
        IStreamProvider streams)
    {
        _runtime = runtime;
        _streams = streams;
    }

    public async Task PromoteAsync(
        string catalogActorId,
        string scriptId,
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
                new PromoteScriptRevisionCommand(
                    ScriptId: scriptId ?? string.Empty,
                    Revision: revision ?? string.Empty,
                    DefinitionActorId: definitionActorId ?? string.Empty,
                    SourceHash: sourceHash ?? string.Empty,
                    ProposalId: proposalId ?? string.Empty),
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
                new RollbackScriptRevisionCommand(
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

        var requestId = Guid.NewGuid().ToString("N");
        var replyStreamId = $"scripting.query.catalog.reply:{requestId}";
        var responseTaskSource = new TaskCompletionSource<ScriptCatalogEntryRespondedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await _streams
            .GetStream(replyStreamId)
            .SubscribeAsync<ScriptCatalogEntryRespondedEvent>(response =>
            {
                if (string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                    responseTaskSource.TrySetResult(response);

                return Task.CompletedTask;
            }, ct);

        await actor.HandleEventAsync(
            _queryAdapter.Map(catalogActorId, requestId, replyStreamId, scriptId),
            ct);

        var response = await WaitForResponseAsync(responseTaskSource.Task, requestId, ct);
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

    private static async Task<ScriptCatalogEntryRespondedEvent> WaitForResponseAsync(
        Task<ScriptCatalogEntryRespondedEvent> responseTask,
        string requestId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(QueryTimeout, timeoutCts.Token);
        var completed = await Task.WhenAny(responseTask, timeoutTask);
        if (!ReferenceEquals(completed, responseTask))
            throw new TimeoutException($"Timeout waiting for script catalog entry query response. request_id={requestId}");

        timeoutCts.Cancel();
        return await responseTask;
    }
}
