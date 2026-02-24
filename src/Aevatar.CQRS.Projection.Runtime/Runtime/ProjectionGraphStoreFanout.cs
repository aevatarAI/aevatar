using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreFanout : IProjectionGraphStore
{
    private readonly IReadOnlyList<IProjectionGraphStore> _stores;
    private readonly IProjectionGraphStore _queryStore;
    private readonly ILogger<ProjectionGraphStoreFanout> _logger;

    public ProjectionGraphStoreFanout(
        IEnumerable<IProjectionStoreRegistration<IProjectionGraphStore>> registrations,
        IServiceProvider serviceProvider,
        ILogger<ProjectionGraphStoreFanout>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        _stores = registrations
            .Select(x => x.Create(serviceProvider))
            .ToList();
        _logger = logger ?? NullLogger<ProjectionGraphStoreFanout>.Instance;

        if (_stores.Count == 0)
        {
            throw new InvalidOperationException(
                "No graph projection store providers are registered.");
        }

        _queryStore = _stores[0];
        _logger.LogInformation(
            "Projection graph fan-out initialized. storeCount={StoreCount}",
            _stores.Count);
    }

    public async Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();

        foreach (var store in _stores)
        {
            ct.ThrowIfCancellationRequested();
            await store.UpsertNodeAsync(node, ct);
        }
    }

    public async Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ct.ThrowIfCancellationRequested();

        foreach (var store in _stores)
        {
            ct.ThrowIfCancellationRequested();
            await store.UpsertEdgeAsync(edge, ct);
        }
    }

    public async Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            ct.ThrowIfCancellationRequested();
            await store.DeleteEdgeAsync(scope, edgeId, ct);
        }
    }

    public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default)
    {
        return _queryStore.GetNeighborsAsync(query, ct);
    }

    public Task<ProjectionGraphSubgraph> GetSubgraphAsync(
        ProjectionGraphQuery query,
        CancellationToken ct = default)
    {
        return _queryStore.GetSubgraphAsync(query, ct);
    }
}
