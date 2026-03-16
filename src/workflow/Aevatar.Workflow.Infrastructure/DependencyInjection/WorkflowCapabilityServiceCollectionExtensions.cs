using Aevatar.Configuration;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Presentation.AGUIAdapter.DependencyInjection;
using Aevatar.Workflow.Projection.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.DependencyInjection;

public static class WorkflowCapabilityServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAevatarWorkflow();
        services.AddWorkflowExecutionProjectionCQRS(options =>
            configuration.GetSection("WorkflowExecutionProjection").Bind(options));
        services.AddWorkflowExecutionAGUIAdapter();
        services.AddWorkflowRunInsightBridgeProjector<WorkflowExecutionRunEventProjector>();
        services.AddWorkflowApplication();
        services.AddWorkflowDefinitionFileSource(options =>
        {
            options.WorkflowDirectories.Add(Path.Combine(AppContext.BaseDirectory, "workflows"));
            options.WorkflowDirectories.Add(Path.Combine(AevatarPaths.RepoRoot, "demos", "Aevatar.Demos.Workflow", "workflows"));
            options.WorkflowDirectories.Add(Path.Combine(AevatarPaths.RepoRoot, "workflows", "turing-completeness"));
            options.WorkflowDirectories.Add(AevatarPaths.RepoRootWorkflows);
            options.WorkflowDirectories.Add(Path.Combine(Directory.GetCurrentDirectory(), "workflows"));
            options.WorkflowDirectories.Add(AevatarPaths.Workflows);
            options.DuplicatePolicy = WorkflowDefinitionDuplicatePolicy.Override;
        });
        services.AddWorkflowInfrastructure(options =>
            configuration.GetSection("WorkflowRunReportExport").Bind(options));
        return services;
    }
}
