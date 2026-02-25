namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionQueryableStoreBinding<TReadModel, in TKey>
    : IProjectionStoreBinding<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel
{
    Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default);

    Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default);

    Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default);
}
