namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionOwnedGraph
{
    public string Scope { get; init; } = string.Empty;

    public string OwnerId { get; init; } = string.Empty;

    public IReadOnlyList<ProjectionGraphNode> Nodes { get; init; } = [];

    public IReadOnlyList<ProjectionGraphEdge> Edges { get; init; } = [];
}
