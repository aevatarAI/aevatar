using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNeo4jGraphStoreRegistration(
        this IServiceCollection services,
        Func<IServiceProvider, Neo4jProjectionGraphStoreOptions> optionsFactory,
        Func<IServiceProvider, string> scopeFactory,
        string providerName = ProjectionProviderNames.Neo4j)
    {
        ArgumentNullException.ThrowIfNull(optionsFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        services.AddSingleton<IProjectionStoreRegistration<IProjectionGraphStore>>(
            new DelegateProjectionStoreRegistration<IProjectionGraphStore>(
                providerName,
                provider => new Neo4jProjectionGraphStore(
                    optionsFactory(provider),
                    scopeFactory(provider),
                    providerName,
                    provider.GetService<ILogger<Neo4jProjectionGraphStore>>())));

        return services;
    }
}
