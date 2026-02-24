using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNeo4jReadModelStoreRegistration<TReadModel, TKey>(
        this IServiceCollection services,
        Func<IServiceProvider, Neo4jProjectionReadModelStoreOptions> optionsFactory,
        string scope,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        string providerName = ProjectionReadModelProviderNames.Neo4j)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionReadModelStoreRegistration<TReadModel, TKey>>(
            new DelegateProjectionReadModelStoreRegistration<TReadModel, TKey>(
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
                    scope,
                    keySelector,
                    keyFormatter,
                    providerName,
                    provider.GetService<ILogger<Neo4jProjectionReadModelStore<TReadModel, TKey>>>())));

        return services;
    }

    public static IServiceCollection AddNeo4jRelationStoreRegistration(
        this IServiceCollection services,
        Func<IServiceProvider, Neo4jProjectionRelationStoreOptions> optionsFactory,
        string scope,
        string providerName = ProjectionReadModelProviderNames.Neo4j)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        services.AddSingleton<IProjectionRelationStoreRegistration>(
            new DelegateProjectionRelationStoreRegistration(
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
                    scope,
                    providerName,
                    provider.GetService<ILogger<Neo4jProjectionRelationStore>>())));

        return services;
    }
}
