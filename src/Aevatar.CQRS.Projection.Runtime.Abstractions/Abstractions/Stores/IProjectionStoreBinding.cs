namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreBinding<in TReadModel, in TKey>
    where TReadModel : class, IProjectionReadModel
{
    string StoreName { get; }

    Task UpsertAsync(TReadModel readModel, CancellationToken ct = default);
}
