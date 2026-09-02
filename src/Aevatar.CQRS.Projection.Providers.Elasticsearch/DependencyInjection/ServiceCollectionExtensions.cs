using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;

public static class ElasticsearchProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddElasticsearchDocumentProjectionRepairStore<TReadModel, TKey>(
        this IServiceCollection services)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        services.AddSingleton<IElasticsearchProjectionDocumentRepairStore<TReadModel, TKey>>(provider =>
            new ElasticsearchProjectionDocumentRepairStore<TReadModel, TKey>(
                provider.GetRequiredService<
                    ElasticsearchProjectionDocumentStore<TReadModel, TKey>>()));
        return services;
    }

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
        services.AddSingleton<IProjectionIndexConsistencyProbe<TReadModel>>(provider =>
            provider.GetRequiredService<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>());
        // Non-generic reconcile target so the startup hosted service can enumerate every ES
        // projection store via IEnumerable<IProjectionIndexReconcileTarget> and self-heal schema
        // drift before the read path is hit. Plain AddSingleton (matching the sibling
        // reader/writer/probe registrations above): each closed generic adds one factory
        // descriptor, and they enumerate together. TryAddEnumerable is NOT usable here - a
        // factory descriptor has no distinct implementation type, so it throws "indistinguishable"
        // at ValidateOnBuild when registered for more than one read model. The per-read-model
        // store registration is already idempotent at the call sites, so duplicates do not occur.
        services.AddSingleton<IProjectionIndexReconcileTarget>(provider =>
            provider.GetRequiredService<ElasticsearchProjectionDocumentStore<TReadModel, TKey>>());

        return services;
    }
}
