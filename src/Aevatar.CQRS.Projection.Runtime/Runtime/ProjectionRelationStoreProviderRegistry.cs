using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionRelationStoreProviderRegistry : IProjectionRelationStoreProviderRegistry
{
    public IReadOnlyList<IProjectionStoreRegistration<IProjectionRelationStore>> GetRegistrations(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider
            .GetServices<IProjectionStoreRegistration<IProjectionRelationStore>>()
            .ToList();
    }
}
