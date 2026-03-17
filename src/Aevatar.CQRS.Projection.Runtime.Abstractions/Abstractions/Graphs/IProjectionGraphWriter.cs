namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphWriter<in TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    Task UpsertAsync(TReadModel readModel, CancellationToken ct = default);
}
