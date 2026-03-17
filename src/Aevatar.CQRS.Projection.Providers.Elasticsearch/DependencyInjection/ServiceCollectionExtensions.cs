using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;

public static class ElasticsearchProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddElasticsearchDocumentProjectionStore<TReadModel, TKey>(
        this IServiceCollection services,
        Func<IServiceProvider, ElasticsearchProjectionDocumentStoreOptions> optionsFactory,
        Func<IServiceProvider, DocumentIndexMetadata> metadataFactory,
        Func<TReadModel, TKey> keySelector,
        Func<TKey, string>? keyFormatter = null,
        Func<TReadModel, string?>? indexScopeSelector = null,
        TypeRegistry? typeRegistry = null)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(metadataFactory);
        ArgumentNullException.ThrowIfNull(keySelector);

        services.AddSingleton<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>(provider =>
            new ElasticsearchProjectionDocumentStore<TReadModel, TKey>(
                optionsFactory(provider),
                metadataFactory(provider),
                keySelector,
                keyFormatter,
                indexScopeSelector,
                typeRegistry,
                provider.GetService<ILogger<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>>()));
        services.AddSingleton<IProjectionDocumentWriter<TReadModel>>(provider =>
            provider.GetRequiredService<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>());
        services.AddSingleton<IProjectionDocumentReader<TReadModel, TKey>>(provider =>
            provider.GetRequiredService<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>());

        return services;
    }
}
