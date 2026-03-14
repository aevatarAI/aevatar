namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionDocumentQueryResult<TReadModel>
{
    public static ProjectionDocumentQueryResult<TReadModel> Empty { get; } = new();

    public IReadOnlyList<TReadModel> Items { get; init; } = [];

    public string? NextCursor { get; init; }

    public long? TotalCount { get; init; }
}
