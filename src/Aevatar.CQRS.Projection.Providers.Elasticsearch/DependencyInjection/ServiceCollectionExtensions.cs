using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddElasticsearchReadModelStoreRegistration<TReadModel, TKey>(
        this IServiceCollection services,
        Func<IServiceProvider, ElasticsearchProjectionReadModelStoreOptions> optionsFactory,
        string indexScope,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        string providerName = ProjectionReadModelProviderNames.Elasticsearch)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(indexScope);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionReadModelStoreRegistration<TReadModel, TKey>>(
            new DelegateProjectionReadModelStoreRegistration<TReadModel, TKey>(
                providerName,
                new ProjectionReadModelProviderCapabilities(
                    providerName,
                    supportsIndexing: true,
                    indexKinds: [ProjectionReadModelIndexKind.Document],
                    supportsAliases: true,
                    supportsSchemaValidation: true),
                provider => new ElasticsearchProjectionReadModelStore<TReadModel, TKey>(
                    optionsFactory(provider),
                    indexScope,
                    keySelector,
                    keyFormatter,
                    providerName,
                    provider.GetService<ILogger<ElasticsearchProjectionReadModelStore<TReadModel, TKey>>>())));

        return services;
    }

    public static IServiceCollection AddElasticsearchRelationStoreRegistration(
        this IServiceCollection services,
        string providerName = ProjectionReadModelProviderNames.Elasticsearch)
    {
        services.AddSingleton<IProjectionRelationStoreRegistration>(
            new DelegateProjectionRelationStoreRegistration(
                providerName,
                new ProjectionReadModelProviderCapabilities(
                    providerName,
                    supportsIndexing: true,
                    indexKinds: [ProjectionReadModelIndexKind.Document],
                    supportsAliases: true,
                    supportsSchemaValidation: true,
                    supportsRelations: false,
                    supportsRelationTraversal: false),
                _ => new ElasticsearchProjectionRelationStore(providerName)));

        return services;
    }
}
