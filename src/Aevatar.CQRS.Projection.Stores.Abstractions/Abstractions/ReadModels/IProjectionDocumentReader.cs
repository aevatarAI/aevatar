namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionDocumentReader<TReadModel, in TKey>
    where TReadModel : class, IProjectionReadModel
{
    Task<TReadModel?> GetAsync(TKey key, CancellationToken ct = default);

    Task<IReadOnlyList<TReadModel>> ListAsync(int take = 50, CancellationToken ct = default);
}
