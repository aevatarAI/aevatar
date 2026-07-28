using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Workflow;

/// <summary>DI registration for Workflow tool provider.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers workflow inspection tools (workflow_status, workflow_artifact_query, workflow_actor_current_state, event_query).
    /// Requires IWorkflowExecutionQueryApplicationService to be registered.
    /// </summary>
    // Refactor (iter105/cluster-105-workflow-artifact-query-still-actor-shaped):
    //   Old pattern: Workflow artifact/report/graph query surfaces still sit under actor inspection and actor-query enablement, even after documents were renamed as artifacts/exports.
    //   New principle: Workflow artifacts have an explicit artifact/export query surface separate from actor current-state query and tool names — graph-only workflow_artifact_query tool on existing execution facade; delete actor-shaped graph wrapper and aliases; rename artifact gate away from actor query.
    public static IServiceCollection AddWorkflowTools(
        this IServiceCollection services,
        Action<WorkflowToolOptions>? configure = null)
    {
        var options = new WorkflowToolOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, WorkflowAgentToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, WorkflowCatalogAgentToolSource>());
        return services;
    }
}
