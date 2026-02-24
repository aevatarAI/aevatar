using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryDocumentStoreRegistration<TReadModel, TKey>(
        this IServiceCollection services,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, object?>? listSortSelector = null,
        int listTakeMax = 200,
        string providerName = ProjectionProviderNames.InMemory)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>(
                providerName,
                new ProjectionProviderCapabilities(
                    providerName,
                    supportsIndexing: true,
                    indexKinds: [ProjectionIndexKind.Document],
                    supportsGraph: false,
                    supportsGraphTraversal: false),
                provider => new InMemoryProjectionReadModelStore<TReadModel, TKey>(
                    keySelector,
                    keyFormatter,
                    listSortSelector,
                    listTakeMax,
                    providerName,
                    provider.GetService<ILogger<InMemoryProjectionReadModelStore<TReadModel, TKey>>>())));

        return services;
    }

    public static IServiceCollection AddInMemoryGraphStoreRegistration(
        this IServiceCollection services,
        string providerName = ProjectionProviderNames.InMemory)
    {
        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                providerName,
                new ProjectionProviderCapabilities(
                    providerName,
                    supportsIndexing: true,
                    indexKinds: [ProjectionIndexKind.Graph],
                    supportsGraph: true,
                    supportsGraphTraversal: true),
                _ => new InMemoryProjectionGraphStore(providerName)));

        return services;
    }
}
