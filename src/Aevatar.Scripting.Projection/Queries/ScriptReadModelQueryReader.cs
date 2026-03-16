using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Queries;

public sealed class ScriptReadModelQueryReader : IScriptReadModelQueryPort
{
    private readonly IProjectionDocumentReader<ScriptReadModelDocument, string> _documentReader;

    public ScriptReadModelQueryReader(IProjectionDocumentReader<ScriptReadModelDocument, string> documentReader)
    {
        _documentReader = documentReader ?? throw new ArgumentNullException(nameof(documentReader));
    }

    public async Task<ScriptReadModelSnapshot?> GetSnapshotAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var document = await _documentReader.GetAsync(actorId, ct);
        return document == null ? null : Map(document);
    }

    public async Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 1000);
        var documents = await _documentReader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = boundedTake,
            },
            ct);
        return documents.Items.Select(Map).ToArray();
    }

    private static ScriptReadModelSnapshot Map(ScriptReadModelDocument document)
    {
        return new ScriptReadModelSnapshot(
            ActorId: document.Id,
            ScriptId: document.ScriptId,
            DefinitionActorId: document.DefinitionActorId,
            Revision: document.Revision,
            ReadModelTypeUrl: document.ReadModelTypeUrl,
            ReadModelPayload: document.ReadModelPayload?.Clone(),
            StateVersion: document.StateVersion,
            LastEventId: document.LastEventId,
            UpdatedAt: document.UpdatedAt);
    }
}
