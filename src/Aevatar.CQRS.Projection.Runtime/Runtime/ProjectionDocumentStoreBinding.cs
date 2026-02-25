namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreBinding<TReadModel, TKey>
    : IProjectionQueryableStoreBinding<TReadModel, TKey>,
      IProjectionStoreBindingAvailability
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionDocumentStore<TReadModel, TKey>? _store;

    public ProjectionDocumentStoreBinding(IProjectionDocumentStore<TReadModel, TKey>? store = null)
    {
        _store = store;
    }

    public bool IsConfigured => _store is not null;

    public string AvailabilityReason => IsConfigured
        ? "Document binding is active."
        : "Document projection store service is not registered.";

    public string StoreName => IsConfigured ? "Document" : "Document(Unconfigured)";

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        if (_store is null)
            return Task.CompletedTask;

        return _store.UpsertAsync(readModel, ct);
    }

    public Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        return GetRequiredStore().MutateAsync(key, mutate, ct);
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        return GetRequiredStore().GetAsync(key, ct);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        return GetRequiredStore().ListAsync(take, ct);
    }

    private IProjectionDocumentStore<TReadModel, TKey> GetRequiredStore()
    {
        return _store ??
               throw new InvalidOperationException(
                   $"Document projection store is not configured for read model '{typeof(TReadModel).FullName}'.");
    }
}
