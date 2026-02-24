namespace Aevatar.CQRS.Projection.Runtime.Abstractions;

public interface IProjectionDocumentStoreFactory
{
    IDocumentProjectionStore<TReadModel, TKey> Create<TReadModel, TKey>(
        IServiceProvider serviceProvider,
        string? requestedProviderName = null)
        where TReadModel : class;
}
