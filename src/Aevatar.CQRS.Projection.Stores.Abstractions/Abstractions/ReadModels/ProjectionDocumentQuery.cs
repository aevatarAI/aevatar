namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionDocumentQuery
{
    public IReadOnlyList<ProjectionDocumentFilter> Filters { get; init; } = [];

    public IReadOnlyList<ProjectionDocumentSort> Sorts { get; init; } = [];

    public string? Cursor { get; init; }

    public int Take { get; init; } = 50;

    public bool IncludeTotalCount { get; init; }
}
