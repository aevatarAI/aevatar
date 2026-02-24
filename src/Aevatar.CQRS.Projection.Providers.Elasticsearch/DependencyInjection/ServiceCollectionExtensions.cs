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
        Func<IServiceProvider, DocumentIndexMetadata> metadataFactory,
        Func<TReadModel, TKey> keySelector,
        bool isPrimaryQueryStore,
        Func<TKey, string>? keyFormatter = null)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(metadataFactory);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>>(
            new DelegateProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>(
                "Elasticsearch",
                isPrimaryQueryStore,
                provider => new ElasticsearchProjectionReadModelStore<TReadModel, TKey>(
                    optionsFactory(provider),
                    metadataFactory(provider),
                    keySelector,
                    keyFormatter,
                    provider.GetService<ILogger<ElasticsearchProjectionReadModelStore<TReadModel, TKey>>>())));

        return services;
    }
}
