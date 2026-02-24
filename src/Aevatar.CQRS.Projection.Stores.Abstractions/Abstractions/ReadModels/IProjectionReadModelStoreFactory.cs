namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionReadModelStoreFactory
{
    IProjectionReadModelStore<TReadModel, TKey> Create<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class;
}
