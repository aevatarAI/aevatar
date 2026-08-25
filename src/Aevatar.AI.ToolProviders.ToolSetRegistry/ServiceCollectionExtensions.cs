using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ToolSetRegistry;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddToolSetRegistry(
        this IServiceCollection services,
        Action<ToolSetRegistryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<ToolSetRegistryOptions>();

        services.TryAddSingleton<IToolSetRegistry, ToolSetRegistry>();
        services.TryAddSingleton<IAgentToolDiscoveryService>(AgentToolDiscoveryService.Instance);
        return services;
    }
}
