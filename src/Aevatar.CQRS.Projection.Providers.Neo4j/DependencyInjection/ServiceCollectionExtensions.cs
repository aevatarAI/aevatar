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
        string providerName = ProjectionReadModelProviderNames.Neo4j)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>>(
            new DelegateProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>(
                providerName,
                new ProjectionReadModelProviderCapabilities(
                    providerName,
                    supportsIndexing: true,
                    indexKinds: [ProjectionReadModelIndexKind.Graph],
                    supportsAliases: false,
                    supportsSchemaValidation: true,
                    supportsRelations: true,
                    supportsRelationTraversal: true),
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
        Func<IServiceProvider, Neo4jProjectionRelationStoreOptions> optionsFactory,
        Func<IServiceProvider, string> scopeFactory,
        string providerName = ProjectionReadModelProviderNames.Neo4j)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        services.AddSingleton<IProjectionStoreRegistration<IProjectionRelationStore>>(
            new DelegateProjectionStoreRegistration<IProjectionRelationStore>(
                providerName,
                new ProjectionReadModelProviderCapabilities(
                    providerName,
                    supportsIndexing: true,
                    indexKinds: [ProjectionReadModelIndexKind.Graph],
                    supportsAliases: false,
                    supportsSchemaValidation: true,
                    supportsRelations: true,
                    supportsRelationTraversal: true),
                provider => new Neo4jProjectionRelationStore(
                    optionsFactory(provider),
                    scopeFactory(provider),
                    providerName,
                    provider.GetService<ILogger<Neo4jProjectionRelationStore>>())));

        return services;
    }
}
