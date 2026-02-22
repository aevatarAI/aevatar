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
        IConfiguration configuration)
    {
        services.AddAevatarConfig();
        services.AddAevatarActorRuntime(configuration);
        RegisterConnectorBuilders(services);
        return services;
    }

    private static void RegisterConnectorBuilders(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, HttpConnectorBuilder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectorBuilder, CliConnectorBuilder>());
    }
}
