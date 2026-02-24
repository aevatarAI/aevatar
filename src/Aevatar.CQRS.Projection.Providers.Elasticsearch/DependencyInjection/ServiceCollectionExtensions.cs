using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddElasticsearchDocumentStoreRegistration<TReadModel, TKey>(
        this IServiceCollection services,
        Func<IServiceProvider, ElasticsearchProjectionReadModelStoreOptions> optionsFactory,
        Func<IServiceProvider, string> indexScopeFactory,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        string providerName = ProjectionProviderNames.Elasticsearch)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(indexScopeFactory);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>(
                providerName,
                provider => new ElasticsearchProjectionReadModelStore<TReadModel, TKey>(
                    optionsFactory(provider),
                    indexScopeFactory(provider),
                    keySelector,
                    keyFormatter,
                    providerName,
                    provider.GetService<ILogger<ElasticsearchProjectionReadModelStore<TReadModel, TKey>>>())));

        return services;
    }
}
