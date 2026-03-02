using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(45);

    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly QueryScriptDefinitionSnapshotRequestAdapter _queryAdapter = new();

    public RuntimeScriptDefinitionSnapshotPort(
        IActorRuntime runtime,
        IStreamProvider streams)
    {
        _runtime = runtime;
        _streams = streams;
    }

    public async Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        var actor = await _runtime.GetAsync(definitionActorId)
            ?? throw new InvalidOperationException($"Script definition actor not found: {definitionActorId}");

        var requestId = Guid.NewGuid().ToString("N");
        var replyStreamId = $"scripting.query.definition.reply:{requestId}";
        var responseTaskSource = new TaskCompletionSource<ScriptDefinitionSnapshotRespondedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await _streams
            .GetStream(replyStreamId)
            .SubscribeAsync<ScriptDefinitionSnapshotRespondedEvent>(response =>
            {
                if (string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
                    responseTaskSource.TrySetResult(response);

                return Task.CompletedTask;
            }, ct);

        await actor.HandleEventAsync(
            _queryAdapter.Map(definitionActorId, requestId, replyStreamId, requestedRevision),
            ct);

        var response = await WaitForResponseAsync(responseTaskSource.Task, requestId, ct);
        if (!response.Found)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.FailureReason)
                    ? $"Script definition snapshot not found for actor `{definitionActorId}`."
                    : response.FailureReason);

        var snapshot = new ScriptDefinitionSnapshot(
            response.ScriptId ?? string.Empty,
            response.Revision ?? string.Empty,
            response.SourceText ?? string.Empty,
            response.ReadModelSchemaVersion ?? string.Empty,
            response.ReadModelSchemaHash ?? string.Empty);

        if (string.IsNullOrWhiteSpace(snapshot.SourceText))
            throw new InvalidOperationException(
                $"Script definition source_text is empty for actor `{definitionActorId}`.");
        if (!string.IsNullOrWhiteSpace(requestedRevision) &&
            !string.Equals(requestedRevision, snapshot.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Requested script revision `{requestedRevision}` does not match definition snapshot revision `{snapshot.Revision}`.");

        ct.ThrowIfCancellationRequested();
        return snapshot;
    }

    private static async Task<ScriptDefinitionSnapshotRespondedEvent> WaitForResponseAsync(
        Task<ScriptDefinitionSnapshotRespondedEvent> responseTask,
        string requestId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(QueryTimeout, timeoutCts.Token);
        var completed = await Task.WhenAny(responseTask, timeoutTask);
        if (!ReferenceEquals(completed, responseTask))
            throw new TimeoutException($"Timeout waiting for script definition snapshot query response. request_id={requestId}");

        timeoutCts.Cancel();
        return await responseTask;
    }
}
