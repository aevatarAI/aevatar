using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>DI registration for Web tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers web search, fetch, and user interaction tools.
    /// </summary>
    public static IServiceCollection AddWebTools(
        this IServiceCollection services,
        Action<WebToolOptions>? configure = null)
    {
        var options = new WebToolOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<WebApiClient>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, WebAgentToolSource>());
        return services;
    }
}
