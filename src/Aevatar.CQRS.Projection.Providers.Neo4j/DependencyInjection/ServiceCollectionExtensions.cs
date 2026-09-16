using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;

public static class Neo4jProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddNeo4jGraphProjectionStore(
        this IServiceCollection services,
        Func<IServiceProvider, Neo4jProjectionGraphStoreOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton<Neo4jProjectionGraphStore>(provider =>
            new Neo4jProjectionGraphStore(
                optionsFactory(provider),
                provider.GetService<ILogger<Neo4jProjectionGraphStore>>()));
        services.AddSingleton<IProjectionGraphStore>(provider =>
            provider.GetRequiredService<Neo4jProjectionGraphStore>());
        services.AddSingleton<IVersionedProjectionGraphStore>(provider =>
            provider.GetRequiredService<Neo4jProjectionGraphStore>());
        services.AddSingleton(new ProjectionGraphProviderStatus("Neo4j", Enabled: true));

        return services;
    }
}
