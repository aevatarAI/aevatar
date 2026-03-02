using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort, IScriptRuntimeDefinitionQueryModePort
{
    private const string OrleansRuntimePrefix = "Aevatar.Foundation.Runtime.Implementations.Orleans.";

    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly TimeSpan _queryTimeout;
    private readonly QueryScriptDefinitionSnapshotRequestAdapter _queryAdapter = new();

    public RuntimeScriptDefinitionSnapshotPort(
        IActorRuntime runtime,
        IStreamProvider streams,
        IScriptingPortTimeouts timeouts)
    {
        _runtime = runtime;
        _streams = streams;
        _queryTimeout = NormalizeTimeout(timeouts.DefinitionSnapshotQueryTimeout);
        var runtimeType = runtime.GetType().FullName;
        UseEventDrivenDefinitionQuery = !string.IsNullOrWhiteSpace(runtimeType) &&
                                        runtimeType.StartsWith(OrleansRuntimePrefix, StringComparison.Ordinal);
    }

    public bool UseEventDrivenDefinitionQuery { get; }

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

    private async Task<ScriptDefinitionSnapshotRespondedEvent> WaitForResponseAsync(
        Task<ScriptDefinitionSnapshotRespondedEvent> responseTask,
        string requestId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeoutTask = Task.Delay(_queryTimeout, timeoutCts.Token);
        var completed = await Task.WhenAny(responseTask, timeoutTask);
        if (!ReferenceEquals(completed, responseTask))
            throw new TimeoutException($"Timeout waiting for script definition snapshot query response. request_id={requestId}");

        timeoutCts.Cancel();
        return await responseTask;
    }

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(45);
}
