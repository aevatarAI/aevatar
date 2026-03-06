using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.Bridge;
using Aevatar.Workflow.Infrastructure.Reporting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowInfrastructure(
        this IServiceCollection services,
        Action<WorkflowExecutionReportArtifactOptions>? configureReports = null)
    {
        services.AddOptions<WorkflowExecutionReportArtifactOptions>();
        services.AddOptions<WorkflowBridgeOptions>();
        if (configureReports != null)
            services.Configure(configureReports);

        // Replace the Noop fallback from Application layer with the real FileSystem sink.
        // Application registers NoopWorkflowExecutionReportArtifactSink via TryAddSingleton;
        // Infrastructure must use Replace to override it.
        services.Replace(ServiceDescriptor.Singleton<IWorkflowExecutionReportArtifactSink, FileSystemWorkflowExecutionReportArtifactSink>());
        services.TryAddSingleton<IWorkflowRunActorPort, WorkflowRunActorPort>();
        services.TryAddSingleton<IBridgeCallbackTokenService, HmacBridgeCallbackTokenService>();
        services.TryAddSingleton<IWorkflowDefinitionResolver, RegistryWorkflowDefinitionResolver>();
        return services;
    }

    public static IServiceCollection AddWorkflowDefinitionFileSource(
        this IServiceCollection services,
        Action<WorkflowDefinitionFileSourceOptions>? configure = null)
    {
        services.AddOptions<WorkflowDefinitionFileSourceOptions>();
        if (configure != null)
            services.Configure(configure);

        services.TryAddSingleton<WorkflowDefinitionFileLoader>();
        services.Replace(ServiceDescriptor.Singleton<FileBackedWorkflowCatalogPort, FileBackedWorkflowCatalogPort>());
        services.Replace(ServiceDescriptor.Singleton<IWorkflowCatalogPort>(sp =>
            sp.GetRequiredService<FileBackedWorkflowCatalogPort>()));
        services.Replace(ServiceDescriptor.Singleton<IWorkflowCapabilitiesPort>(sp =>
            sp.GetRequiredService<FileBackedWorkflowCatalogPort>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowDefinitionBootstrapHostedService>());
        return services;
    }
}
