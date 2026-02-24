namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionRelationStore
{
    Task UpsertNodeAsync(ProjectionRelationNode node, CancellationToken ct = default);

    Task UpsertEdgeAsync(ProjectionRelationEdge edge, CancellationToken ct = default);

    Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default);

    Task<IReadOnlyList<ProjectionRelationEdge>> GetNeighborsAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default);

    Task<ProjectionRelationSubgraph> GetSubgraphAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default);
}
