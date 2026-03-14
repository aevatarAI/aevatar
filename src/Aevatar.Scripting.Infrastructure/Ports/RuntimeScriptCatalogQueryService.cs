using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogQueryService : IScriptCatalogQueryPort
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);
    private readonly RuntimeScriptActorQueryClient? _queryClient;
    private readonly Func<string?, string, CancellationToken, Task<ScriptCatalogEntryRespondedEvent>>? _queryAsync;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public RuntimeScriptCatalogQueryService(
        RuntimeScriptActorQueryClient queryClient,
        IScriptingActorAddressResolver addressResolver)
    {
        _queryClient = queryClient ?? throw new ArgumentNullException(nameof(queryClient));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    internal RuntimeScriptCatalogQueryService(
        Func<string?, string, CancellationToken, Task<ScriptCatalogEntryRespondedEvent>> queryAsync,
        IScriptingActorAddressResolver addressResolver)
    {
        _queryAsync = queryAsync ?? throw new ArgumentNullException(nameof(queryAsync));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        string? catalogActorId,
        string scriptId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
            return null;

        var resolvedCatalogActorId = string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : catalogActorId;
        var response = _queryAsync != null
            ? await _queryAsync(resolvedCatalogActorId, scriptId, ct)
            : await _queryClient!.QueryActorAsync<ScriptCatalogEntryRespondedEvent>(
                resolvedCatalogActorId,
                ScriptActorQueryRouteConventions.CatalogEntryReplyStreamPrefix,
                QueryTimeout,
                (requestId, replyStreamId) => ScriptActorQueryEnvelopeFactory.CreateCatalogEntryQuery(
                    resolvedCatalogActorId,
                    requestId,
                    replyStreamId,
                    scriptId),
                static (reply, requestId) => string.Equals(reply.RequestId, requestId, StringComparison.Ordinal),
                ScriptActorQueryRouteConventions.BuildCatalogTimeoutMessage,
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
