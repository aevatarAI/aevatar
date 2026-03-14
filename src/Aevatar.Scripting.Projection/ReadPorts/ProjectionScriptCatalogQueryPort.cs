using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.ReadPorts;

public sealed class ProjectionScriptCatalogQueryPort : IScriptCatalogQueryPort
{
    private readonly ScriptAuthorityProjectionPortService? _projectionPort;
    private readonly IProjectionDocumentStore<ScriptCatalogEntryDocument, string>? _documentStore;
    private readonly IScriptingActorAddressResolver? _addressResolver;
    private readonly Func<string?, string, CancellationToken, Task<ScriptCatalogEntrySnapshot?>>? _queryAsync;

    public ProjectionScriptCatalogQueryPort(
        ScriptAuthorityProjectionPortService projectionPort,
        IProjectionDocumentStore<ScriptCatalogEntryDocument, string> documentStore,
        IScriptingActorAddressResolver addressResolver)
    {
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    internal ProjectionScriptCatalogQueryPort(
        Func<string?, string, CancellationToken, Task<ScriptCatalogEntrySnapshot?>> queryAsync)
    {
        _queryAsync = queryAsync ?? throw new ArgumentNullException(nameof(queryAsync));
    }

    public async Task<ScriptCatalogEntrySnapshot?> GetCatalogEntryAsync(
        string? catalogActorId,
        string scriptId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scriptId))
            return null;

        if (_queryAsync != null)
            return await _queryAsync(catalogActorId, scriptId, ct);

        var resolvedCatalogActorId = string.IsNullOrWhiteSpace(catalogActorId)
            ? _addressResolver!.GetCatalogActorId()
            : catalogActorId;
        _ = await _projectionPort!.EnsureActorProjectionAsync(resolvedCatalogActorId, ct);

        var document = await _documentStore!.GetAsync(
            ScriptCatalogEntryProjector.BuildDocumentId(resolvedCatalogActorId, scriptId),
            ct);
        if (document == null || string.IsNullOrWhiteSpace(document.ActiveRevision))
            return null;

        return new ScriptCatalogEntrySnapshot(
            document.ScriptId,
            document.ActiveRevision,
            document.ActiveDefinitionActorId,
            document.ActiveSourceHash,
            document.PreviousRevision,
            document.RevisionHistory.ToArray(),
            document.LastProposalId);
    }
}
