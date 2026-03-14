namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionGraphMaterializer<in TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    ProjectionGraphMaterialization Materialize(TReadModel readModel);
}
