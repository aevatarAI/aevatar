using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>DI registration for NyxID tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NyxID tool system. When BaseUrl is configured, all NyxID management
    /// tools are automatically available to any AIGAgentBase-derived agent.
    /// </summary>
    public static IServiceCollection AddNyxIdTools(
        this IServiceCollection services,
        Action<NyxIdToolOptions> configure)
    {
        var options = new NyxIdToolOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, NyxIdAgentToolSource>());
        return services;
    }
}
