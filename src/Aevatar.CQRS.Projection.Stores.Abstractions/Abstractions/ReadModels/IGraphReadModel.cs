namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IGraphReadModel : IProjectionReadModel
{
    string GraphScope { get; }

    IReadOnlyList<GraphNodeDescriptor> GraphNodes { get; }

    IReadOnlyList<GraphEdgeDescriptor> GraphEdges { get; }
}
