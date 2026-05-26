using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.AgentCatalog;

/// <summary>
/// DI registration entry point for the agent-catalog tool provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent-delivery-target tool source so LLM turns can resolve the
    /// catalog of user-owned agents available as delivery targets.
    /// </summary>
    public static IServiceCollection AddAgentCatalogTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, AgentDeliveryTargetToolSource>());

        return services;
    }
}
