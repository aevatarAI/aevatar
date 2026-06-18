namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionIndexConsistencyProbe<TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    Task<ProjectionIndexConsistencyResult> CheckIndexConsistencyAsync(CancellationToken ct = default);
}
