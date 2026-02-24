namespace Aevatar.CQRS.Projection.Abstractions;

public sealed class ProjectionRelationSubgraph
{
    public ProjectionRelationSubgraph()
    {
        Nodes = [];
        Edges = [];
    }

    public IReadOnlyList<ProjectionRelationNode> Nodes { get; set; }

    public IReadOnlyList<ProjectionRelationEdge> Edges { get; set; }
}
