using Aevatar.Bootstrap.Connectors;
using Aevatar.Configuration;
using Aevatar.Foundation.Runtime.Hosting.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Bootstrap;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarBootstrap(
        this IServiceCollection services,
        IConfiguration configuration,
        bool allowLocalFileSecretsStore = true)
    {
        // EnvironmentSecretsStore (registered by AddAevatarConfig when
        // allowLocalFileSecretsStore=false) ctor-injects IConfiguration. The
        // overload here already receives the instance, so register it into DI
        // up front instead of forcing every caller to add it themselves.
        // TryAdd preserves any existing registration from a host builder.
        services.TryAddSingleton(configuration);
        services.AddAevatarConfig(allowLocalFileSecretsStore);
        services.AddHttpClient();
        services.AddAevatarActorRuntime(configuration);
        RegisterConnectorBuilders(services);
        return services;
    }

    private static void RegisterConnectorBuilders(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, HttpConnectorBuilder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, CliConnectorBuilder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, TelegramUserConnectorBuilder>());
    }
}
