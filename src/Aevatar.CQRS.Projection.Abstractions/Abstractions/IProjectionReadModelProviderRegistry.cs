namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionReadModelProviderRegistry
{
    IReadOnlyList<IProjectionReadModelStoreRegistration<TReadModel, TKey>> GetRegistrations<TReadModel, TKey>(
        IServiceProvider serviceProvider)
        where TReadModel : class;
}
