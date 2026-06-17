using Aevatar.CQRS.Projection.Stores.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;

internal sealed class ElasticsearchIndexStartupInitializer<TReadModel, TKey>(
    ElasticsearchProjectionDocumentStore<TReadModel, TKey> store,
    ILogger<ElasticsearchIndexStartupInitializer<TReadModel, TKey>> logger)
    : IHostedService
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.EnsureStaticIndexLifecycleAsync(cancellationToken);
        logger.LogInformation(
            "Elasticsearch projection index startup initializer completed. readModelType={ReadModelType}",
            typeof(TReadModel).FullName);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
