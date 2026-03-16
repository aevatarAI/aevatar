namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionDocumentSort
{
    public string FieldPath { get; init; } = "";

    public ProjectionDocumentSortDirection Direction { get; init; }
}
