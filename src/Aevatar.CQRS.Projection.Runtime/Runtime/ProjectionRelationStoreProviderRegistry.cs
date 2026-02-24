using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionRelationStoreProviderRegistry : IProjectionRelationStoreProviderRegistry
{
    public IReadOnlyList<IProjectionRelationStoreRegistration> GetRegistrations(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider
            .GetServices<IProjectionRelationStoreRegistration>()
            .ToList();
    }
}
