namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionReadModelCloneable<out TReadModel>
    where TReadModel : class, IProjectionReadModel
{
    TReadModel DeepClone();
}
