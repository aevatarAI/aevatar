using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Runtime.Runtime;

public sealed class ProjectionDocumentStoreProviderRegistry : IProjectionDocumentStoreProviderRegistry
{
    public IReadOnlyList<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>> GetRegistrations<TReadModel, TKey>(
        IServiceProvider serviceProvider)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return serviceProvider
            .GetServices<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>>()
            .ToList();
    }
}
