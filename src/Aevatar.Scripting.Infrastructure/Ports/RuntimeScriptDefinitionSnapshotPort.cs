using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RuntimeScriptQueryClient _queryClient;
    private readonly TimeSpan _queryTimeout;

    public RuntimeScriptDefinitionSnapshotPort(
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        ScriptingQueryTimeoutOptions timeoutOptions)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _queryTimeout = (timeoutOptions ?? throw new ArgumentNullException(nameof(timeoutOptions)))
            .ResolveDefinitionSnapshotQueryTimeout();
    }

    public async Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        var actor = await _actorAccessor.GetAsync(definitionActorId)
            ?? throw new InvalidOperationException($"Script definition actor not found: {definitionActorId}");

        var response = await _queryClient.QueryActorAsync<ScriptDefinitionSnapshotRespondedEvent>(
            actor,
            ScriptingQueryRouteConventions.DefinitionReplyStreamPrefix,
            _queryTimeout,
            (requestId, replyStreamId) => ScriptingQueryEnvelopeFactory.CreateDefinitionSnapshotQuery(
                definitionActorId,
                requestId,
                replyStreamId,
                requestedRevision),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildDefinitionSnapshotTimeoutMessage,
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
            response.SourceHash ?? string.Empty,
            response.StateTypeUrl ?? string.Empty,
            response.ReadModelTypeUrl ?? string.Empty,
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
}
