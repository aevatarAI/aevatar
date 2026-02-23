namespace Aevatar.CQRS.Projection.Abstractions;

public interface IProjectionReadModelStoreRegistration<TReadModel, TKey>
    where TReadModel : class
{
    string ProviderName { get; }

    ProjectionReadModelProviderCapabilities Capabilities { get; }

    IProjectionReadModelStore<TReadModel, TKey> Create(IServiceProvider serviceProvider);
}
