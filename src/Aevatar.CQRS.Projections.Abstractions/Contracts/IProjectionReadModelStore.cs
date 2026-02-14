namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Generic read-model store contract for projection state persistence/query.
/// </summary>
public interface IProjectionReadModelStore<TReadModel, in TKey>
    where TReadModel : class
{
    Task UpsertAsync(TReadModel readModel, CancellationToken ct = default);

    Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default);

    Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default);

    Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default);
}
