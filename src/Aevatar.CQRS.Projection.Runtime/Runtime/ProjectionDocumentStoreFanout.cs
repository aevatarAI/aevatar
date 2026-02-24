using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreFanout<TReadModel, TKey>
    : IDocumentProjectionStore<TReadModel, TKey>
    where TReadModel : class
{
    private readonly IReadOnlyList<IDocumentProjectionStore<TReadModel, TKey>> _stores;
    private readonly IReadOnlyList<IDocumentProjectionStore<TReadModel, TKey>> _replicaStores;
    private readonly IDocumentProjectionStore<TReadModel, TKey> _queryStore;
    private readonly string _queryProviderName;
    private readonly ILogger<ProjectionDocumentStoreFanout<TReadModel, TKey>> _logger;

    public ProjectionDocumentStoreFanout(
        IEnumerable<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> registrations,
        IServiceProvider serviceProvider,
        ILogger<ProjectionDocumentStoreFanout<TReadModel, TKey>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registrationList = registrations.ToList();
        var resolvedStores = registrationList
            .Select(x => x.Create(serviceProvider))
            .ToList();
        _stores = resolvedStores;
        _logger = logger ?? NullLogger<ProjectionDocumentStoreFanout<TReadModel, TKey>>.Instance;

        if (_stores.Count == 0)
        {
            throw new InvalidOperationException(
                $"No document projection store providers are registered for read model '{typeof(TReadModel).FullName}'.");
        }

        var primaryRegistrations = registrationList
            .Where(x => x.IsPrimaryQueryStore)
            .ToList();
        if (primaryRegistrations.Count == 0 && registrationList.Count > 1)
        {
            var providers = string.Join(", ", registrationList.Select(x => x.ProviderName));
            throw new InvalidOperationException(
                $"Exactly one primary document projection store provider must be configured for read model '{typeof(TReadModel).FullName}'. registeredProviders=[{providers}]");
        }

        if (primaryRegistrations.Count > 1)
        {
            var providers = string.Join(", ", primaryRegistrations.Select(x => x.ProviderName));
            throw new InvalidOperationException(
                $"Multiple primary document projection store providers are configured for read model '{typeof(TReadModel).FullName}'. primaryProviders=[{providers}]");
        }

        var queryRegistration = primaryRegistrations.Count == 1
            ? primaryRegistrations[0]
            : registrationList[0];
        var queryIndex = registrationList.FindIndex(x => ReferenceEquals(x, queryRegistration));
        if (queryIndex < 0)
        {
            throw new InvalidOperationException(
                $"Failed to resolve primary document projection store provider for read model '{typeof(TReadModel).FullName}'.");
        }

        _queryStore = _stores[queryIndex];
        _queryProviderName = queryRegistration.ProviderName;

        var replicaStores = new List<IDocumentProjectionStore<TReadModel, TKey>>(_stores.Count);
        for (var i = 0; i < _stores.Count; i++)
        {
            if (i == queryIndex)
                continue;
            replicaStores.Add(_stores[i]);
        }

        _replicaStores = replicaStores;
        _logger.LogInformation(
            "Projection document fan-out initialized. readModelType={ReadModelType} storeCount={StoreCount} queryProvider={QueryProvider}",
            typeof(TReadModel).FullName,
            _stores.Count,
            _queryProviderName);
    }

    public async Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ct.ThrowIfCancellationRequested();

        foreach (var store in _stores)
        {
            ct.ThrowIfCancellationRequested();
            await store.UpsertAsync(readModel, ct);
        }
    }

    public async Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ct.ThrowIfCancellationRequested();

        await _queryStore.MutateAsync(key, mutate, ct);
        if (_replicaStores.Count == 0)
            return;

        var updated = await _queryStore.GetAsync(key, ct);
        if (updated == null)
        {
            throw new InvalidOperationException(
                $"Document fan-out mutate completed but query store returned null for read model '{typeof(TReadModel).FullName}'.");
        }

        foreach (var store in _replicaStores)
        {
            ct.ThrowIfCancellationRequested();
            await store.UpsertAsync(updated, ct);
        }
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        return _queryStore.GetAsync(key, ct);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        return _queryStore.ListAsync(take, ct);
    }
}
