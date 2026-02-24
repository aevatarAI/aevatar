namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IGraphReadModel : IProjectionReadModel
{
    string GraphScope { get; }

    IReadOnlyList<ProjectionGraphNode> GraphNodes { get; }

    IReadOnlyList<ProjectionGraphEdge> GraphEdges { get; }
}
