namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionDocumentWriter<in TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    Task UpsertAsync(TReadModel readModel, CancellationToken ct = default);
}
