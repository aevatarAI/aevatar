using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Infrastructure.Capabilities;
using Aevatar.Workflow.Infrastructure.Reporting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Projection.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Workflow.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowInfrastructure(
        this IServiceCollection services,
        Action<WorkflowRunReportExportOptions>? configureReportExport = null)
    {
        services.AddOptions<WorkflowRunReportExportOptions>();
        if (configureReportExport != null)
            services.Configure(configureReportExport);
        services.AddOptions<FileSystemWorkflowFileIngressOptions>();

        // Replace the Noop fallback from Application layer with the real file export adapter.
        services.Replace(ServiceDescriptor.Singleton<IWorkflowRunReportExportPort, FileSystemWorkflowRunReportExporter>());
        services.TryAddSingleton<FileSystemWorkflowFileIngressPort>();
        services.TryAddSingleton<IWorkflowFileIngressPort>(sp =>
            sp.GetRequiredService<FileSystemWorkflowFileIngressPort>());
        services.TryAddSingleton<IWorkflowFileArtifactReadPort>(sp =>
            sp.GetRequiredService<FileSystemWorkflowFileIngressPort>());
        services.TryAddSingleton<IWorkflowFileArtifactOwnershipPort>(sp =>
            sp.GetRequiredService<FileSystemWorkflowFileIngressPort>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowToolSource, WorkflowDocumentExtractToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowToolSource, WorkflowConnectedServiceResourceFetchToolSource>());
        services.TryAddSingleton<WorkflowRunActorPort>();
        services.TryAddSingleton<IWorkflowDefinitionProvisioningPort>(sp =>
            sp.GetRequiredService<WorkflowRunActorPort>());
        services.TryAddSingleton<IWorkflowRunProvisioningPort>(sp =>
            sp.GetRequiredService<WorkflowRunActorPort>());
        services.TryAddSingleton<IWorkflowDefinitionParser>(sp =>
            sp.GetRequiredService<WorkflowRunActorPort>());
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
            sp.GetRequiredService<WorkflowCatalogReadModelQueryPort>()));
        services.TryAddSingleton<WorkflowInfrastructureCapabilitiesProvider>();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowCapabilitiesPort>(sp =>
            sp.GetRequiredService<WorkflowInfrastructureCapabilitiesProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowDefinitionBootstrapHostedService>());
        return services;
    }
}
