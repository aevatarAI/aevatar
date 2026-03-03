namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public sealed class ProjectionGraphSubgraph
{
    public ProjectionGraphSubgraph()
    {
        Nodes = [];
        Edges = [];
    }

    public IReadOnlyList<ProjectionGraphNode> Nodes { get; set; }

    public IReadOnlyList<ProjectionGraphEdge> Edges { get; set; }
}
