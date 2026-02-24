using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddElasticsearchDocumentProjectionStore<TReadModel, TKey>(
        this IServiceCollection services,
        Func<IServiceProvider, ElasticsearchProjectionDocumentStoreOptions> optionsFactory,
        Func<IServiceProvider, DocumentIndexMetadata> metadataFactory,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null)
        where TReadModel : class
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(metadataFactory);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<IProjectionDocumentStore<TReadModel, TKey>>(provider =>
            new ElasticsearchProjectionDocumentStore<TReadModel, TKey>(
                optionsFactory(provider),
                metadataFactory(provider),
                keySelector,
                keyFormatter,
                provider.GetService<ILogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>>()));

        return services;
    }
}
