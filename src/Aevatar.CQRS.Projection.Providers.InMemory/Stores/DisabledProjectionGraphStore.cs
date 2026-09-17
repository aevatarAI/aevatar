namespace Aevatar.CQRS.Projection.Providers.InMemory.Stores;

public sealed class DisabledProjectionGraphStore
    : IProjectionGraphStore,
      IVersionedProjectionGraphStore
{
    private const string DisabledDetail = "Graph projection is disabled by configuration.";

    public Task ReplaceOwnerGraphAsync(
        ProjectionOwnedGraph graph,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task UpsertNodeAsync(
        ProjectionGraphNode node,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task UpsertEdgeAsync(
        ProjectionGraphEdge edge,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DeleteNodeAsync(
        string scope,
        string nodeId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DeleteEdgeAsync(
        string scope,
        string edgeId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
        string scope,
        string ownerId,
        int skip = 0,
        int take = 5000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProjectionGraphNode>>([]);
    }

    public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
        string scope,
        string ownerId,
        int skip = 0,
        int take = 5000,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);
    }

    public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ProjectionGraphEdge>>([]);
    }

    public Task<ProjectionGraphSubgraph> GetSubgraphAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ProjectionGraphSubgraph());
    }

    public Task<ProjectionGraphDeltaApplyResult> ApplyDeltaAsync(
        ProjectionGraphDelta delta,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ProjectionGraphDeltaApplyResult
        {
            Disposition = ProjectionGraphDeltaApplyDisposition.Applied,
            Detail = DisabledDetail,
        });
    }

    public Task<ProjectionGraphOwnerSnapshotReadResult> ReadOwnerSnapshotAsync(
        ProjectionGraphRouteFingerprint route,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ProjectionGraphOwnerSnapshotReadResult
        {
            Disposition = ProjectionGraphOwnerSnapshotReadDisposition.NotFound,
            Detail = DisabledDetail,
        });
    }
}
