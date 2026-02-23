using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionReadModelProviderRegistry : IProjectionReadModelProviderRegistry
{
    public IReadOnlyList<IProjectionReadModelStoreRegistration<TReadModel, TKey>> GetRegistrations<TReadModel, TKey>(
        IServiceProvider serviceProvider)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider
            .GetServices<IProjectionReadModelStoreRegistration<TReadModel, TKey>>()
            .ToList();
    }
}
