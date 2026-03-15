namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionDocumentFilter
{
    public string FieldPath { get; init; } = "";

    public ProjectionDocumentFilterOperator Operator { get; init; }

    public ProjectionDocumentValue Value { get; init; } = ProjectionDocumentValue.Empty;
}
