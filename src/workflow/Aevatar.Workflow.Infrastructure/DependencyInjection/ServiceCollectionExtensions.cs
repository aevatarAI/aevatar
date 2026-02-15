using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Infrastructure.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowInfrastructure(
        this IServiceCollection services,
        Action<WorkflowExecutionReportArtifactOptions>? configureReports = null)
    {
        services.AddOptions<WorkflowExecutionReportArtifactOptions>();
        if (configureReports != null)
            services.Configure(configureReports);

        services.AddSingleton<IWorkflowExecutionReportArtifactSink, FileSystemWorkflowExecutionReportArtifactSink>();
        return services;
    }
}
