using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>DI registration for Web tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers web search and fetch tools.
    /// </summary>
    public static IServiceCollection AddWebTools(
        this IServiceCollection services,
        Action<WebToolOptions>? configure = null)
    {
        // Refactor (iter10/cluster-019):
        // Old: WebApiClient was a singleton that could own a raw HttpClient forever.
        // New: WebApiClient is an AddHttpClient<T> typed client with factory-managed handler lifetime.
        var options = new WebToolOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.AddHttpClient<WebApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });
        services.TryAddTransient<IWebApiClient>(sp => sp.GetRequiredService<WebApiClient>());
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IAgentToolSource, WebAgentToolSource>());
        return services;
    }
}
