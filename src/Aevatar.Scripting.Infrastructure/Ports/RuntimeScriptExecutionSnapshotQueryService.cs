using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptExecutionSnapshotQueryService : IScriptExecutionProjectionQueryPort
{
    private readonly RuntimeScriptActorAccessor _actorAccessor;
    private readonly RuntimeScriptQueryClient _queryClient;
    private readonly IScriptDefinitionSnapshotPort _definitionSnapshotPort;
    private readonly TimeSpan _queryTimeout;
    private readonly QueryScriptRuntimeSnapshotRequestAdapter _queryAdapter = new();

    public RuntimeScriptExecutionSnapshotQueryService(
        RuntimeScriptActorAccessor actorAccessor,
        RuntimeScriptQueryClient queryClient,
        IScriptDefinitionSnapshotPort definitionSnapshotPort,
        IScriptingPortTimeouts timeouts)
    {
        _actorAccessor = actorAccessor ?? throw new ArgumentNullException(nameof(actorAccessor));
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _definitionSnapshotPort = definitionSnapshotPort ?? throw new ArgumentNullException(nameof(definitionSnapshotPort));
        _queryTimeout = (timeouts ?? throw new ArgumentNullException(nameof(timeouts)))
            .GetRuntimeSnapshotQueryTimeout();
    }

    public async Task<ScriptExecutionSnapshot?> GetRuntimeSnapshotAsync(
        string runtimeActorId,
        CancellationToken ct = default)
    {
        var normalizedRuntimeActorId = runtimeActorId?.Trim() ?? string.Empty;
        if (normalizedRuntimeActorId.Length == 0)
            return null;

        var actor = await _actorAccessor.GetAsync(normalizedRuntimeActorId);
        if (actor == null)
            return null;

        var response = await _queryClient.QueryActorAsync<ScriptRuntimeSnapshotRespondedEvent>(
            actor,
            ScriptingQueryRouteConventions.RuntimeReplyStreamPrefix,
            _queryTimeout,
            (requestId, replyStreamId) => _queryAdapter.Map(normalizedRuntimeActorId, requestId, replyStreamId),
            static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
            ScriptingQueryRouteConventions.BuildRuntimeSnapshotTimeoutMessage,
            ct);
        if (!response.Found)
            return null;

        var scriptId = string.Empty;
        if (!string.IsNullOrWhiteSpace(response.DefinitionActorId))
        {
            try
            {
                var definition = await _definitionSnapshotPort.GetRequiredAsync(
                    response.DefinitionActorId,
                    response.Revision ?? string.Empty,
                    ct);
                scriptId = definition.ScriptId;
            }
            catch
            {
                scriptId = string.Empty;
            }
        }

        return new ScriptExecutionSnapshot
        {
            RuntimeActorId = response.RuntimeActorId ?? string.Empty,
            ScriptId = scriptId,
            DefinitionActorId = response.DefinitionActorId ?? string.Empty,
            Revision = response.Revision ?? string.Empty,
            ReadModelSchemaVersion = response.ReadModelSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = response.ReadModelSchemaHash ?? string.Empty,
            LastRunId = response.LastRunId ?? string.Empty,
            LastEventType = response.LastEventType ?? string.Empty,
            LastDomainEventPayload = response.LastDomainEventPayload?.Clone(),
            StateVersion = response.StateVersion,
            LastEventId = response.LastEventId ?? string.Empty,
            StatePayloads = response.StatePayloads.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Clone(),
                StringComparer.Ordinal),
            ReadModelPayloads = response.ReadModelPayloads.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Clone(),
                StringComparer.Ordinal),
        };
    }
}
