using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.ChronoStorage;

/// <summary>DI registration for ChronoStorage tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ChronoStorage tool system. When ApiBaseUrl is configured,
    /// all chrono-storage file browsing and editing tools are automatically
    /// available to any AIGAgentBase-derived agent.
    /// </summary>
    public static IServiceCollection AddChronoStorageTools(
        this IServiceCollection services,
        Action<ChronoStorageToolOptions> configure)
    {
        var options = new ChronoStorageToolOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ChronoStorageApiClient>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ChronoStorageAgentToolSource>());
        return services;
    }
}
