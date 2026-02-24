namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionDocumentStoreProviderRegistry
{
    IReadOnlyList<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> GetRegistrations<TReadModel, TKey>(
        IServiceProvider serviceProvider)
        where TReadModel : class;
}
