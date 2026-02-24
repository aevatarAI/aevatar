using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionReadModelProviderRegistry : IProjectionReadModelProviderRegistry
{
    public IReadOnlyList<IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>> GetRegistrations<TReadModel, TKey>(
        IServiceProvider serviceProvider)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider
            .GetServices<IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>>()
            .ToList();
    }
}
