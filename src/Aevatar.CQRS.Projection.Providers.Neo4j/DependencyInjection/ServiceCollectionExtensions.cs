using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNeo4jGraphProjectionStore(
        this IServiceCollection services,
        Func<IServiceProvider, Neo4jProjectionGraphStoreOptions> optionsFactory)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);

        services.AddSingleton<IProjectionGraphStore>(provider =>
            new Neo4jProjectionGraphStore(
                optionsFactory(provider),
                provider.GetService<ILogger<Neo4jProjectionGraphStore>>()));

        return services;
    }
}
