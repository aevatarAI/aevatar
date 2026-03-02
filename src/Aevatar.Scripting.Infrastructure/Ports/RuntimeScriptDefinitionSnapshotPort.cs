using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
{
    private readonly IActorRuntime _runtime;
    private readonly IStreamProvider _streams;
    private readonly TimeSpan _queryTimeout;
    private readonly bool _useEventDrivenDefinitionQuery;
    private readonly QueryScriptDefinitionSnapshotRequestAdapter _queryAdapter = new();

    public RuntimeScriptDefinitionSnapshotPort(
        IActorRuntime runtime,
        IStreamProvider streams,
        IScriptingRuntimeQueryModes queryModes,
        IScriptingPortTimeouts timeouts)
    {
        _runtime = runtime;
        _streams = streams;
        _useEventDrivenDefinitionQuery = (queryModes ?? throw new ArgumentNullException(nameof(queryModes)))
            .UseEventDrivenDefinitionQuery;
        _queryTimeout = NormalizeTimeout(timeouts.DefinitionSnapshotQueryTimeout);
    }

    public bool UseEventDrivenDefinitionQuery => _useEventDrivenDefinitionQuery;

    public async Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        var actor = await _runtime.GetAsync(definitionActorId)
            ?? throw new InvalidOperationException($"Script definition actor not found: {definitionActorId}");

        var response = await ScriptQueryReplyAwaiter.QueryAsync<ScriptDefinitionSnapshotRespondedEvent>(
            _streams,
            "scripting.query.definition.reply",
            _queryTimeout,
            (requestId, replyStreamId) => actor.HandleEventAsync(
                _queryAdapter.Map(definitionActorId, requestId, replyStreamId, requestedRevision),
                ct),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            static requestId => $"Timeout waiting for script definition snapshot query response. request_id={requestId}",
            ct);
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

    private static TimeSpan NormalizeTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(45);
}
