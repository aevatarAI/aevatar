namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionDocumentWriter<in TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default);

    Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default);
}
