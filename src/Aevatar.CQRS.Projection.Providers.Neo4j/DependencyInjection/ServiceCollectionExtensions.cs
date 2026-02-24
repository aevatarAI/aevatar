using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNeo4jDocumentStoreRegistration<TReadModel, TKey>(
        this IServiceCollection services,
        Func<IServiceProvider, Neo4jProjectionReadModelStoreOptions> optionsFactory,
        Func<IServiceProvider, string> scopeFactory,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        string providerName = ProjectionProviderNames.Neo4j)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>(
                providerName,
                new ProjectionProviderCapabilities(
                    providerName,
                    supportsIndexing: true,
                    indexKinds: [ProjectionIndexKind.Graph],
                    supportsAliases: false,
                    supportsSchemaValidation: true,
                    supportsGraph: true,
                    supportsGraphTraversal: true),
                provider => new Neo4jProjectionReadModelStore<TReadModel, TKey>(
                    optionsFactory(provider),
                    scopeFactory(provider),
                    keySelector,
                    keyFormatter,
                    providerName,
                    provider.GetService<ILogger<Neo4jProjectionReadModelStore<TReadModel, TKey>>>())));

        return services;
    }

    public static IServiceCollection AddNeo4jGraphStoreRegistration(
        this IServiceCollection services,
        Func<IServiceProvider, Neo4jProjectionGraphStoreOptions> optionsFactory,
        Func<IServiceProvider, string> scopeFactory,
        string providerName = ProjectionProviderNames.Neo4j)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                providerName,
                new ProjectionProviderCapabilities(
                    providerName,
                    supportsIndexing: true,
                    indexKinds: [ProjectionIndexKind.Graph],
                    supportsAliases: false,
                    supportsSchemaValidation: true,
                    supportsGraph: true,
                    supportsGraphTraversal: true),
                provider => new Neo4jProjectionGraphStore(
                    optionsFactory(provider),
                    scopeFactory(provider),
                    providerName,
                    provider.GetService<ILogger<Neo4jProjectionGraphStore>>())));

        return services;
    }
}
