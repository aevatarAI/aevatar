using Aevatar.Bootstrap.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Bootstrap;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarBootstrap(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAevatarConfig();
        services.AddAevatarActorRuntime(configuration);
        RegisterConnectorBuilders(services);
        services.AddConfiguredConnectorCatalog();
        return services;
    }

    public static IServiceCollection AddConfiguredConnectorCatalog(
        this IServiceCollection services,
        Action<ConfiguredConnectorCatalogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ConfiguredConnectorCatalogOptions();
        configure?.Invoke(options);

        services.Replace(ServiceDescriptor.Singleton<IConnectorCatalog>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = loggerFactory?.CreateLogger("Aevatar.Connectors") ?? NullLogger.Instance;
            var configPaths = new List<string?>();
            if (options.IncludeDefaultHomeConfig)
                configPaths.Add(null);

            foreach (var path in options.AdditionalConfigPaths)
                configPaths.Add(path);

            return ConnectorCatalogFactory.Build(
                sp.GetServices<IConnectorBuilder>(),
                logger,
                configPaths);
        }));

        return services;
    }

    private static void RegisterConnectorBuilders(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, HttpConnectorBuilder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, CliConnectorBuilder>());
    }
}
