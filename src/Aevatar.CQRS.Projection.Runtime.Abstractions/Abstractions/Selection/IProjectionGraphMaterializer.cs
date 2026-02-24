namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphMaterializer<in TReadModel>
    where TReadModel : class
{
    Task UpsertGraphAsync(TReadModel readModel, CancellationToken ct = default);
}
