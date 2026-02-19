using Aevatar.Configuration;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Presentation.AGUIAdapter.DependencyInjection;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Sagas.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.DependencyInjection;

public static class WorkflowSubsystemServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowSubsystem(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddWorkflowExecutionProjectionCQRS(options =>
            configuration.GetSection("WorkflowExecutionProjection").Bind(options));
        services.AddWorkflowExecutionSagas();
        services.AddWorkflowExecutionAGUIAdapter();
        services.AddWorkflowExecutionProjectionProjector<WorkflowExecutionAGUIEventProjector>();
        services.AddWorkflowApplication();
        services.AddWorkflowDefinitionFileSource(options =>
        {
            options.WorkflowDirectories.Add(Path.Combine(AppContext.BaseDirectory, "workflows"));
            options.WorkflowDirectories.Add(AevatarPaths.RepoRootWorkflows);
            options.WorkflowDirectories.Add(Path.Combine(Directory.GetCurrentDirectory(), "workflows"));
            options.WorkflowDirectories.Add(AevatarPaths.Workflows);
        });
        services.AddWorkflowInfrastructure(options =>
            configuration.GetSection("WorkflowExecutionReportArtifacts").Bind(options));
        return services;
    }
}
