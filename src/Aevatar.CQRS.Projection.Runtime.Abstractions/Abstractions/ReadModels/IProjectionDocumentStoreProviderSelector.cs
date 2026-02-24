namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionDocumentStoreProviderSelector
{
    IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>> Select<TReadModel, TKey>(
        IReadOnlyList<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> registrations,
        ProjectionStoreSelectionOptions selectionOptions,
        ProjectionStoreRequirements requirements)
        where TReadModel : class;
}
