using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Infrastructure.Capabilities;
using Aevatar.Workflow.Infrastructure.Reporting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Infrastructure.Schedules;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Workflow.Projection.Workflows;
using Aevatar.GAgentService.Abstractions.Ports;
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
        DecorateWorkflowScheduledDispatchPort(services);
        services.TryAddSingleton<IScheduledDispatchActorPort, ScheduledDispatchActorPort>();
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

    private static void DecorateWorkflowScheduledDispatchPort(IServiceCollection services)
    {
        var existing = services.LastOrDefault(static descriptor => descriptor.ServiceType == typeof(IActorDispatchPort));
        if (existing == null)
            return;

        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(
            typeof(ScheduledDispatchTransportDelegate),
            sp => new ScheduledDispatchTransportDelegate(CreateInnerDispatchPort(existing, sp)),
            existing.Lifetime));
        services.Add(ServiceDescriptor.Describe(
            typeof(IActorDispatchPort),
            sp => new WorkflowScheduledDispatchAdapterPort(
                sp.GetRequiredService<ScheduledDispatchTransportDelegate>().Inner,
                () => sp.GetRequiredService<IWorkflowRunActorResolver>(),
                () => sp.GetRequiredService<ICommandEnvelopeFactory<WorkflowChatRunRequest>>(),
                () => sp.GetService<IServiceInvocationPort>()),
            existing.Lifetime));
    }

    private static IActorDispatchPort CreateInnerDispatchPort(
        ServiceDescriptor descriptor,
        IServiceProvider serviceProvider)
    {
        if (descriptor.ImplementationInstance is IActorDispatchPort instance)
            return instance;

        if (descriptor.ImplementationFactory != null)
            return (IActorDispatchPort)descriptor.ImplementationFactory(serviceProvider)!;

        if (descriptor.ImplementationType != null)
            return (IActorDispatchPort)ActivatorUtilities.CreateInstance(
                serviceProvider,
                descriptor.ImplementationType);

        throw new InvalidOperationException("IActorDispatchPort registration is not supported.");
    }

    private sealed class ScheduledDispatchTransportDelegate(IActorDispatchPort inner)
    {
        public IActorDispatchPort Inner { get; } = inner;
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
