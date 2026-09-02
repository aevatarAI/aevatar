namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionWriteDispatcher<in TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default);

    Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default);

    Task<ProjectionWriteResult> DeleteAsync(
        ProjectionDocumentDeleteMarker marker,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"Projection write dispatcher '{GetType().FullName}' does not support versioned read-model deletes.");
}
