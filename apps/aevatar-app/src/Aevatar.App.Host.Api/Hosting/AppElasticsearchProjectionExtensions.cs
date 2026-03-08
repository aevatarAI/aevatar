using Aevatar.App.Application.Projection.ReadModels;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.App.Host.Api.Hosting;

public static class AppElasticsearchProjectionExtensions
{
    /// <summary>
    /// Registers Elasticsearch-backed projection stores for all App read models.
    /// Must be called BEFORE <c>AddAppProjection()</c> so that
    /// <c>TryAddSingleton</c> in the InMemory fallback is skipped.
    /// </summary>
    public static IServiceCollection AddAppElasticsearchProjection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("App:Projection:Elasticsearch");

        ElasticsearchProjectionDocumentStoreOptions BuildOptions()
        {
            var opts = new ElasticsearchProjectionDocumentStoreOptions();
            section.Bind(opts);
            if (opts.Endpoints.Count == 0)
                opts.Endpoints.Add("http://localhost:9200");
            return opts;
        }

        services.AddElasticsearchDocumentProjectionStore<AppUserAccountReadModel, string>(
            _ => BuildOptions(), _ => IndexMeta("user-accounts"), m => m.Id);

        services.AddElasticsearchDocumentProjectionStore<AppUserProfileReadModel, string>(
            _ => BuildOptions(), _ => IndexMeta("user-profiles"), m => m.Id);

        services.AddElasticsearchDocumentProjectionStore<AppSyncEntityReadModel, string>(
            _ => BuildOptions(), _ => SyncEntitiesIndexMeta(), m => m.Id);

        services.AddElasticsearchDocumentProjectionStore<AppAuthLookupReadModel, string>(
            _ => BuildOptions(), _ => IndexMeta("auth-lookups"), m => m.Id);

        services.AddElasticsearchDocumentProjectionStore<AppSyncEntityLastResultReadModel, string>(
            _ => BuildOptions(), _ => IndexMeta("sync-last-results"), m => m.Id);

        services.AddElasticsearchDocumentProjectionStore<AppUserAffiliateReadModel, string>(
            _ => BuildOptions(), _ => IndexMeta("user-affiliates"), m => m.Id);

        services.AddElasticsearchDocumentProjectionStore<AppPaymentTransactionReadModel, string>(
            _ => BuildOptions(), _ => IndexMeta("payment-transactions"), m => m.Id);

        return services;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyDict
        = new Dictionary<string, object?>();

    private static DocumentIndexMetadata IndexMeta(string name)
        => new(name, EmptyDict, EmptyDict, EmptyDict);

    /// <summary>
    /// Entities is a Dictionary keyed by dynamic clientId strings.
    /// Each key generates unique ES field mappings, quickly exceeding the
    /// default 1000 total_fields limit. Setting <c>enabled: false</c> on
    /// the Entities property tells ES to store the JSON as-is without
    /// creating per-key field mappings.
    /// </summary>
    private static DocumentIndexMetadata SyncEntitiesIndexMeta()
    {
        var mappings = new Dictionary<string, object?>
        {
            ["properties"] = new Dictionary<string, object?>
            {
                ["Entities"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["enabled"] = false
                }
            }
        };
        return new DocumentIndexMetadata("sync-entities", mappings, EmptyDict, EmptyDict);
    }
}
