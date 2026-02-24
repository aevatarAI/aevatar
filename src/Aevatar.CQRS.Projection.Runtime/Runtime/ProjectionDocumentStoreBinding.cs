namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreBinding<TReadModel, TKey>
    : IProjectionQueryableStoreBinding<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    private readonly IProjectionDocumentStore<TReadModel, TKey> _store;

    public ProjectionDocumentStoreBinding(IProjectionDocumentStore<TReadModel, TKey> store)
    {
        _store = store;
    }

    public string StoreName => "Document";

    public Task UpsertAsync(TReadModel readModel, CancellationToken ct = default)
    {
        return _store.UpsertAsync(readModel, ct);
    }

    public Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default)
    {
        return _store.MutateAsync(key, mutate, ct);
    }

    public Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default)
    {
        return _store.GetAsync(key, ct);
    }

    public Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        return _store.ListAsync(take, ct);
    }
}
