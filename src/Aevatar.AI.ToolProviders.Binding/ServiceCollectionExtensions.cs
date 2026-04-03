using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Binding;

/// <summary>DI registration for Binding tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register binding management tools (binding_list, binding_status, binding_bind, binding_unbind).
    /// Port implementations (IScopeBindingCommandPort, IScopeBindingQueryAdapter,
    /// IScopeBindingUnbindAdapter) must be registered separately by the infrastructure layer.
    /// </summary>
    public static IServiceCollection AddBindingTools(
        this IServiceCollection services,
        Action<BindingToolOptions>? configure = null)
    {
        var options = new BindingToolOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, BindingAgentToolSource>());
        return services;
    }
}
