using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionGraphStoreProviderRegistry : IProjectionGraphStoreProviderRegistry
{
    public IReadOnlyList<IProjectionStoreRegistration<IProjectionGraphStore>> GetRegistrations(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider
            .GetServices<IProjectionStoreRegistration<IProjectionGraphStore>>()
            .ToList();
    }
}
