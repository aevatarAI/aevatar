namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionStoreStartupValidator
{
    IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>> ValidateReadModelProvider<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class;

    IProjectionStoreRegistration<IProjectionRelationStore> ValidateRelationProvider(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements);
}
