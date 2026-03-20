using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Projection.ReadModels;
using Google.Protobuf;

namespace Aevatar.Scripting.Projection.ReadPorts;

public sealed class ProjectionScriptDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
{
    private readonly IProjectionDocumentReader<ScriptDefinitionSnapshotDocument, string>? _documentReader;
    private readonly Func<string, string, CancellationToken, Task<ScriptDefinitionSnapshot?>>? _queryAsync;

    public ProjectionScriptDefinitionSnapshotPort(
        IProjectionDocumentReader<ScriptDefinitionSnapshotDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    internal ProjectionScriptDefinitionSnapshotPort(
        Func<string, string, CancellationToken, Task<ScriptDefinitionSnapshot?>> queryAsync)
    {
        _queryAsync = queryAsync ?? throw new ArgumentNullException(nameof(queryAsync));
    }

    public async Task<ScriptDefinitionSnapshot?> TryGetAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionActorId);

        if (_queryAsync != null)
            return await _queryAsync(definitionActorId, requestedRevision, ct);

        var document = await _documentReader!.GetAsync(definitionActorId, ct);
        if (document == null || string.IsNullOrWhiteSpace(document.Revision))
            return null;

        if (!string.IsNullOrWhiteSpace(requestedRevision) &&
            !string.Equals(requestedRevision, document.Revision, StringComparison.Ordinal))
        {
            return null;
        }

        var protocolDescriptorSet = string.IsNullOrWhiteSpace(document.ProtocolDescriptorSetBase64)
            ? ByteString.Empty
            : ByteString.FromBase64(document.ProtocolDescriptorSetBase64);

        return new ScriptDefinitionSnapshot(
            document.ScriptId,
            document.Revision,
            document.SourceText,
            document.SourceHash,
            document.ScriptPackage?.Clone() ?? new ScriptPackageSpec(),
            document.StateTypeUrl,
            document.ReadModelTypeUrl,
            document.ReadModelSchemaVersion,
            document.ReadModelSchemaHash,
            protocolDescriptorSet,
            document.StateDescriptorFullName,
            document.ReadModelDescriptorFullName,
            document.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec(),
            document.DefinitionActorId,
            document.ScopeId);
    }

    public async Task<ScriptDefinitionSnapshot> GetRequiredAsync(
        string definitionActorId,
        string requestedRevision,
        CancellationToken ct)
    {
        var snapshot = await TryGetAsync(definitionActorId, requestedRevision, ct);
        if (snapshot == null)
        {
            throw new InvalidOperationException(
                $"Script definition snapshot not found for actor `{definitionActorId}` revision `{requestedRevision}`.");
        }

        if ((snapshot.ScriptPackage?.CsharpSources.Count ?? 0) == 0 && string.IsNullOrWhiteSpace(snapshot.SourceText))
        {
            throw new InvalidOperationException(
                $"Script definition script_package is empty for actor `{definitionActorId}`.");
        }

        return snapshot;
    }
}
