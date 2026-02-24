namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionDocumentStoreFactory
{
    IDocumentProjectionStore<TReadModel, TKey> Create<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements)
        where TReadModel : class;
}
