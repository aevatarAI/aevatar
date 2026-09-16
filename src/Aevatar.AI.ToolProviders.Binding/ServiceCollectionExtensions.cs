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

    /// <summary>
    /// Registers the explicit, read-only workflow external-capability authoring source. The source
    /// is intentionally concrete-only so hosts must opt into it through a named tool set.
    /// </summary>
    public static IServiceCollection AddWorkflowExternalCapabilityAuthoringTools(
        this IServiceCollection services,
        Action<WorkflowExternalCapabilityToolOptions>? configure = null)
    {
        var options = new WorkflowExternalCapabilityToolOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<WorkflowExternalCapabilityAuthoringToolSource>();
        return services;
    }
}
