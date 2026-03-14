using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);
    private readonly RuntimeScriptActorQueryClient? _queryClient;
    private readonly Func<string, string, CancellationToken, Task<ScriptDefinitionSnapshotRespondedEvent>>? _queryAsync;

    public RuntimeScriptDefinitionSnapshotPort(RuntimeScriptActorQueryClient queryClient)
    {
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
    }

    internal RuntimeScriptDefinitionSnapshotPort(
        Func<string, string, CancellationToken, Task<ScriptDefinitionSnapshotRespondedEvent>> queryAsync)
    {
        _queryAsync = queryAsync ?? throw new ArgumentNullException(nameof(queryAsync));
    }

    public async Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        var response = _queryAsync != null
            ? await _queryAsync(definitionActorId, requestedRevision, ct)
            : await _queryClient!.QueryActorAsync<ScriptDefinitionSnapshotRespondedEvent>(
                definitionActorId,
                ScriptActorQueryRouteConventions.DefinitionSnapshotReplyStreamPrefix,
                QueryTimeout,
                (requestId, replyStreamId) => ScriptActorQueryEnvelopeFactory.CreateDefinitionSnapshotQuery(
                    definitionActorId,
                    requestId,
                    replyStreamId,
                    requestedRevision ?? string.Empty),
                static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
                ScriptActorQueryRouteConventions.BuildDefinitionTimeoutMessage,
                ct);

        if (!response.Found)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.FailureReason)
                    ? $"Script definition snapshot not found for actor `{definitionActorId}`."
                    : response.FailureReason);
        }

        if ((response.ScriptPackage?.CsharpSources.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(response.SourceText))
        {
            throw new InvalidOperationException(
                $"Script definition script_package is empty for actor `{definitionActorId}`.");
        }

        return new ScriptDefinitionSnapshot(
            response.ScriptId ?? string.Empty,
            response.Revision ?? string.Empty,
            response.SourceText ?? string.Empty,
            response.SourceHash ?? string.Empty,
            response.ScriptPackage?.Clone() ?? new Aevatar.Scripting.Abstractions.ScriptPackageSpec(),
            response.StateTypeUrl ?? string.Empty,
            response.ReadModelTypeUrl ?? string.Empty,
            response.ReadModelSchemaVersion ?? string.Empty,
            response.ReadModelSchemaHash ?? string.Empty,
            response.ProtocolDescriptorSet,
            response.StateDescriptorFullName ?? string.Empty,
            response.ReadModelDescriptorFullName ?? string.Empty,
            response.RuntimeSemantics?.Clone() ?? new Aevatar.Scripting.Abstractions.ScriptRuntimeSemanticsSpec());
    }
}
