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
        // Refactor (iter10/cluster-019):
        // Old: ChronoStorageApiClient was a singleton that could own a raw HttpClient forever.
        // New: ChronoStorageApiClient is an AddHttpClient<T> typed client with factory-managed handler lifetime.
        var options = new ChronoStorageToolOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.AddHttpClient<ChronoStorageApiClient>();
        services.TryAddTransient<ChronoStorageReadAgentToolSource>();
        services.TryAddTransient<ChronoStorageWriteAgentToolSource>();
        return services;
    }
}
