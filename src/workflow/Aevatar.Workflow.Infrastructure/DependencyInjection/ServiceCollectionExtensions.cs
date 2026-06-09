using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Schedules;
using Aevatar.Workflow.Infrastructure.Reporting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Infrastructure.Schedules;
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

        // Replace the Noop fallback from Application layer with the real file export adapter.
        services.Replace(ServiceDescriptor.Singleton<IWorkflowRunReportExportPort, FileSystemWorkflowRunReportExporter>());
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

    public static IServiceCollection AddWorkflowScheduleInfrastructure(
        this IServiceCollection services,
        Action<WorkflowScheduleStoreOptions>? configureStore = null)
    {
        services.AddOptions<WorkflowScheduleStoreOptions>();
        if (configureStore != null)
            services.Configure(configureStore);

        services.TryAddSingleton<IWorkflowScheduleStore, FileWorkflowScheduleStore>();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowScheduleCredentialExchangePort>(sp =>
        {
            var broker = sp.GetService<INyxIdCapabilityBroker>();
            return broker == null
                ? new NoopWorkflowScheduleCredentialExchangePort()
                : ActivatorUtilities.CreateInstance<NyxIdWorkflowScheduleCredentialExchangePort>(sp, broker);
        }));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowScheduleDispatcherHostedService>());
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
        services.TryAddSingleton<WorkflowCapabilitiesStartupMaterializer>();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowCatalogPort>(sp =>
            sp.GetRequiredService<WorkflowCatalogReadModelQueryPort>()));
        services.Replace(ServiceDescriptor.Singleton<IWorkflowCapabilitiesPort>(sp =>
            sp.GetRequiredService<WorkflowCatalogReadModelQueryPort>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowDefinitionBootstrapHostedService>());
        return services;
    }
}
