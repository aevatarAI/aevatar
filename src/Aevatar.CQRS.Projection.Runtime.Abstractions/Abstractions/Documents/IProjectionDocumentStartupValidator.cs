namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionDocumentStartupValidator
{
    IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> ValidateProvider<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionDocumentSelectionOptions selectionOptions)
        where TReadModel : class;
}
