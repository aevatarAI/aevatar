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
    /// Also registers <see cref="NyxIdToolApprovalHandler"/> as the
    /// <see cref="IToolApprovalHandler"/> so agents can route tool approvals
    /// through NyxID (Telegram / mobile app).
    /// </summary>
    public static IServiceCollection AddNyxIdTools(
        this IServiceCollection services,
        Action<NyxIdToolOptions> configure)
    {
        // Refactor (iter10/cluster-019):
        // Old: singleton tool clients constructed or pinned raw HttpClient instances.
        // New: stateless API calls use AddHttpClient<T>; stateful caches use named clients through IHttpClientFactory.
        var options = new NyxIdToolOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.AddHttpClient<NyxIdApiClient>();
        services.AddHttpClient(NyxIdSpecCatalog.HttpClientName, _ => { });
        services.AddHttpClient(ConnectedServiceSpecCache.HttpClientName, _ => { });
        services.TryAddSingleton<NyxIdSpecCatalog>();
        services.TryAddSingleton<IConnectedServiceSpecSource, ConnectedServiceSpecCache>();
        services.TryAddSingleton<IServiceDiscoveryCache, InMemoryServiceDiscoveryCache>();
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IAgentToolSource, NyxIdAgentToolSource>());

        // Remote approval handler for timeout escalation (NyxID Telegram/app push).
        services.TryAddTransient<IToolApprovalHandler>(sp =>
            new NyxIdToolApprovalHandler(sp.GetRequiredService<NyxIdApiClient>()));

        return services;
    }
}
