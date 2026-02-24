namespace Aevatar.CQRS.Projection.Stores.Abstractions;

public interface IProjectionReadModelProviderRegistry
{
    IReadOnlyList<IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>> GetRegistrations<TReadModel, TKey>(
        IServiceProvider serviceProvider)
        where TReadModel : class;
}
