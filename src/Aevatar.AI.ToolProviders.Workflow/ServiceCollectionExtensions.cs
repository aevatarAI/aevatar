using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Workflow;

/// <summary>DI registration for Workflow tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers workflow inspection tools (workflow_status, actor_inspect, event_query).
    /// Requires IWorkflowExecutionQueryApplicationService to be registered.
    /// </summary>
    public static IServiceCollection AddWorkflowTools(
        this IServiceCollection services,
        Action<WorkflowToolOptions>? configure = null)
    {
        var options = new WorkflowToolOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, WorkflowAgentToolSource>());
        return services;
    }
}
