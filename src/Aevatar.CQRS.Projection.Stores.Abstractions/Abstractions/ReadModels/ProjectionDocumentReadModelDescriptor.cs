namespace Aevatar.CQRS.Projection.Stores.Abstractions;

// Generic inventory descriptor over a document read-model. Store-agnostic: it delegates to that
// read-model's IProjectionDocumentReader<TReadModel, string>, so it works identically for any backing
// document provider (Elasticsearch, InMemory, ...). The host registers ONE of these per document
// read-model at its store-registration site, supplying the Name/Engine/ActorKind it knows there.
//
// CaptureAsync issues a single bounded read-model query (newest-updated first, take 1, total count):
//   - Count            <- TotalCount (the store's IncludeTotalCount).
//   - MaxStateVersion  <- the newest-updated document's StateVersion.
//   - LatestUpdatedAt  <- the newest-updated document's UpdatedAt.
// Current-state replicas are written with monotonic, version-keyed cover semantics, so the most
// recently updated document also carries the highest StateVersion. The query reads the materialized
// document store ONLY; it never replays events or touches IEventStore.
public sealed class ProjectionDocumentReadModelDescriptor<TReadModel> : IProjectionReadModelDescriptor
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionDocumentReader<TReadModel, string> _reader;

    public ProjectionDocumentReadModelDescriptor(
        string name,
        ProjectionReadModelSinkShape shape,
        string engine,
        string actorKind,
        IProjectionDocumentReader<TReadModel, string> reader)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Read-model name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(engine))
            throw new ArgumentException("Engine label is required.", nameof(engine));
        if (string.IsNullOrWhiteSpace(actorKind))
            throw new ArgumentException("Actor kind is required.", nameof(actorKind));

        Name = name;
        Shape = shape;
        Engine = engine;
        ActorKind = actorKind;
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public string Name { get; }

    public ProjectionReadModelSinkShape Shape { get; }

    public string Engine { get; }

    public string ActorKind { get; }

    public async Task<ProjectionReadModelInventorySnapshot> CaptureAsync(CancellationToken ct = default)
    {
        var result = await _reader.QueryAsync(
            new ProjectionDocumentQuery
            {
                Take = 1,
                IncludeTotalCount = true,
                Sorts =
                [
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(IProjectionReadModel.UpdatedAt),
                        Direction = ProjectionDocumentSortDirection.Desc,
                    },
                    new ProjectionDocumentSort
                    {
                        FieldPath = nameof(IProjectionReadModel.StateVersion),
                        Direction = ProjectionDocumentSortDirection.Desc,
                    },
                ],
            },
            ct);

        var newest = result.Items.Count > 0 ? result.Items[0] : null;
        return new ProjectionReadModelInventorySnapshot(
            Count: result.TotalCount,
            MaxStateVersion: newest?.StateVersion,
            LatestUpdatedAt: newest is null ? null : NormalizeUpdatedAt(newest.UpdatedAt));
    }

    // UpdatedAt defaults to default(DateTimeOffset) when a read-model has no timestamp set; surface
    // that as null rather than a fabricated 0001-01-01 instant.
    private static DateTimeOffset? NormalizeUpdatedAt(DateTimeOffset updatedAt) =>
        updatedAt == default ? null : updatedAt;
}
