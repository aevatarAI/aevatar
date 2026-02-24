using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInMemoryReadModelStoreRegistration<TReadModel, TKey>(
        this IServiceCollection services,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, object?>? listSortSelector = null,
        int listTakeMax = 200,
        string providerName = ProjectionReadModelProviderNames.InMemory)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>>(
            new DelegateProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>(
                providerName,
                new ProjectionReadModelProviderCapabilities(
                    providerName,
                    supportsIndexing: false,
                    supportsRelations: true,
                    supportsRelationTraversal: true),
                provider => new InMemoryProjectionReadModelStore<TReadModel, TKey>(
                    keySelector,
                    keyFormatter,
                    listSortSelector,
                    listTakeMax,
                    providerName,
                    provider.GetService<ILogger<InMemoryProjectionReadModelStore<TReadModel, TKey>>>())));

        return services;
    }

    public static IServiceCollection AddInMemoryRelationStoreRegistration(
        this IServiceCollection services,
        string providerName = ProjectionReadModelProviderNames.InMemory)
    {
        services.AddSingleton<IProjectionStoreRegistration<IProjectionRelationStore>>(
            new DelegateProjectionStoreRegistration<IProjectionRelationStore>(
                providerName,
                new ProjectionReadModelProviderCapabilities(
                    providerName,
                    supportsIndexing: false,
                    supportsRelations: true,
                    supportsRelationTraversal: true),
                _ => new InMemoryProjectionRelationStore(providerName)));

        return services;
    }
}
