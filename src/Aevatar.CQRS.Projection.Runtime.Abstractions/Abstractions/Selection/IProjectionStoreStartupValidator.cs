namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionStoreStartupValidator
{
    IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> ValidateDocumentProvider<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements)
        where TReadModel : class;

    IProjectionStoreRegistration<IProjectionGraphStore> ValidateGraphProvider(
        IServiceProvider serviceProvider,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements);
}
