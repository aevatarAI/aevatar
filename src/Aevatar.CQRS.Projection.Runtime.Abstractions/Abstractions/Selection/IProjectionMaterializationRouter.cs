namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionMaterializationRouter<TReadModel, in TKey>
    where TReadModel : class, IProjectionReadModel
{
    Task UpsertAsync(TReadModel readModel, CancellationToken ct = default);

    Task MutateAsync(TKey key, Action<TReadModel> mutate, CancellationToken ct = default);

    Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default);

    Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default);
}
