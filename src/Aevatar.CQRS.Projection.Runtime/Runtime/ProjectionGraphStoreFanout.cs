using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreFanout : IProjectionGraphStore
{
    private readonly IReadOnlyList<IProjectionGraphStore> _stores;
    private readonly IProjectionGraphStore _queryStore;
    private readonly string _queryProviderName;
    private readonly ILogger<ProjectionGraphStoreFanout> _logger;

    public ProjectionGraphStoreFanout(
        IEnumerable<IProjectionStoreRegistration<IProjectionGraphStore>> registrations,
        IServiceProvider serviceProvider,
        ILogger<ProjectionGraphStoreFanout>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registrationList = registrations.ToList();
        _stores = registrationList
            .Select(x => x.Create(serviceProvider))
            .ToList();
        _logger = logger ?? NullLogger<ProjectionGraphStoreFanout>.Instance;

        if (_stores.Count == 0)
        {
            throw new InvalidOperationException(
                "No graph projection store providers are registered.");
        }
        _queryStore = _stores[0];
        _queryProviderName = registrationList[0].ProviderName;

        _logger.LogInformation(
            "Projection graph fan-out initialized. storeCount={StoreCount} queryProvider={QueryProvider}",
            _stores.Count,
            _queryProviderName);
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

    public async Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            ct.ThrowIfCancellationRequested();
            await store.DeleteNodeAsync(scope, nodeId, ct);
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

    public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(
        string scope,
        string ownerId,
        int take = 5000,
        CancellationToken ct = default)
    {
        return _queryStore.ListNodesByOwnerAsync(scope, ownerId, take, ct);
    }

    public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(
        string scope,
        string ownerId,
        int take = 5000,
        CancellationToken ct = default)
    {
        return _queryStore.ListEdgesByOwnerAsync(scope, ownerId, take, ct);
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
