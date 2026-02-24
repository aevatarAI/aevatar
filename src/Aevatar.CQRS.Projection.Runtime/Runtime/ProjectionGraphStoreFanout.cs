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

        var primaryRegistrations = registrationList
            .Where(x => x.IsPrimaryQueryStore)
            .ToList();
        if (primaryRegistrations.Count == 0 && registrationList.Count > 1)
        {
            var providers = string.Join(", ", registrationList.Select(x => x.ProviderName));
            throw new InvalidOperationException(
                $"Exactly one primary graph projection store provider must be configured. registeredProviders=[{providers}]");
        }

        if (primaryRegistrations.Count > 1)
        {
            var providers = string.Join(", ", primaryRegistrations.Select(x => x.ProviderName));
            throw new InvalidOperationException(
                $"Multiple primary graph projection store providers are configured. primaryProviders=[{providers}]");
        }

        var queryRegistration = primaryRegistrations.Count == 1
            ? primaryRegistrations[0]
            : registrationList[0];
        var queryIndex = registrationList.FindIndex(x => ReferenceEquals(x, queryRegistration));
        if (queryIndex < 0)
        {
            throw new InvalidOperationException("Failed to resolve primary graph projection store provider.");
        }

        _queryStore = _stores[queryIndex];
        _queryProviderName = queryRegistration.ProviderName;

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

    public async Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default)
    {
        foreach (var store in _stores)
        {
            ct.ThrowIfCancellationRequested();
            await store.DeleteEdgeAsync(scope, edgeId, ct);
        }
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
