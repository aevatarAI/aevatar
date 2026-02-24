using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreFanout<TReadModel, TKey>
    : IDocumentProjectionStore<TReadModel, TKey>
    where TReadModel : class
{
    private readonly IReadOnlyList<IDocumentProjectionStore<TReadModel, TKey>> _stores;
    private readonly IDocumentProjectionStore<TReadModel, TKey> _queryStore;
    private readonly ILogger<ProjectionDocumentStoreFanout<TReadModel, TKey>> _logger;

    public ProjectionDocumentStoreFanout(
        IEnumerable<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> registrations,
        IServiceProvider serviceProvider,
        ILogger<ProjectionDocumentStoreFanout<TReadModel, TKey>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var registrationList = registrations.ToList();
        _stores = registrationList
            .Select(x => x.Create(serviceProvider))
            .ToList();
        _logger = logger ?? NullLogger<ProjectionDocumentStoreFanout<TReadModel, TKey>>.Instance;

        if (_stores.Count == 0)
        {
            throw new InvalidOperationException(
                $"No document projection store providers are registered for read model '{typeof(TReadModel).FullName}'.");
        }

        _queryStore = _stores[0];
        _logger.LogInformation(
            "Projection document fan-out initialized. readModelType={ReadModelType} storeCount={StoreCount}",
            typeof(TReadModel).FullName,
            _stores.Count);
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
        if (_stores.Count == 1)
            return;

        var updated = await _queryStore.GetAsync(key, ct);
        if (updated == null)
        {
            throw new InvalidOperationException(
                $"Document fan-out mutate completed but query store returned null for read model '{typeof(TReadModel).FullName}'.");
        }

        foreach (var store in _stores.Skip(1))
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
