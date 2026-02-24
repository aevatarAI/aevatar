namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionGraphStore
{
    Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default);

    Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default);

    Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default);

    Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default);

    Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
        string scope,
        string ownerId,
        int take = 5000,
        CancellationToken ct = default);

    Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
        string scope,
        string ownerId,
        int take = 5000,
        CancellationToken ct = default);

    Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default);

    Task<ProjectionGraphSubgraph> GetSubgraphAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default);
}
