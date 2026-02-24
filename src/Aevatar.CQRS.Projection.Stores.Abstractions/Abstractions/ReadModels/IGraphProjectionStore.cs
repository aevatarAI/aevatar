namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IGraphProjectionStore<in TReadModel>
    where TReadModel : class
{
    Task UpsertGraphAsync(TReadModel readModel, CancellationToken ct = default);

    Task<IReadOnlyList<ProjectionRelationEdge>> GetNeighborsAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default);

    Task<ProjectionRelationSubgraph> GetSubgraphAsync(
        ProjectionRelationQuery query,
        CancellationToken ct = default);
}
