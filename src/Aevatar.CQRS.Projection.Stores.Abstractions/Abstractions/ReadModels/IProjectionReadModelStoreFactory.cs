namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionReadModelStoreFactory
{
    IProjectionReadModelStore<TReadModel, TKey> Create<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class;
}
