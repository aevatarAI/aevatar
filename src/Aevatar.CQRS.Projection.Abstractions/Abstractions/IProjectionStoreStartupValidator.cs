namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionStoreStartupValidator
{
    IProjectionReadModelStoreRegistration<TReadModel, TKey> ValidateReadModelProvider<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements)
        where TReadModel : class;

    IProjectionRelationStoreRegistration ValidateRelationProvider(
        IServiceProvider serviceProvider,
        ProjectionReadModelStoreSelectionOptions selectionOptions,
        ProjectionReadModelRequirements requirements);
}
