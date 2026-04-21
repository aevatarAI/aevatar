namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionWriteDispatcher<in TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default);

    Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default);
}
