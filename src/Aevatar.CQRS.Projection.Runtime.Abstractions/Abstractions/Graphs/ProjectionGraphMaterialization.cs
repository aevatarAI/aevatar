namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public sealed class ProjectionGraphMaterialization
{
    public string Scope { get; init; } = string.Empty;

    public IReadOnlyList<ProjectionGraphNode> Nodes { get; init; } = [];

    public IReadOnlyList<ProjectionGraphEdge> Edges { get; init; } = [];
}
