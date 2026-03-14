namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionWriteDispatcher<TReadModel, in TKey>
    where TReadModel : class, IProjectionReadModel
{
    Task UpsertAsync(TReadModel readModel, CancellationToken ct = default);
}
