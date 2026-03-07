using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Workflows;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.Connectors;
using Aevatar.Workflow.Infrastructure.Reporting;
using Aevatar.Workflow.Infrastructure.Runs;
using Aevatar.Workflow.Infrastructure.Workflows;
using Aevatar.Foundation.Abstractions.Connectors;
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
        if (configureReports != null)
            services.Configure(configureReports);

        // Replace the Noop fallback from Application layer with the real FileSystem sink.
        // Application registers NoopWorkflowExecutionReportArtifactSink via TryAddSingleton;
        // Infrastructure must use Replace to override it.
        services.Replace(ServiceDescriptor.Singleton<IWorkflowExecutionReportArtifactSink, FileSystemWorkflowExecutionReportArtifactSink>());
        services.TryAddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
        services.TryAddSingleton<IWorkflowRunActorPort, WorkflowRunActorPort>();
        services.TryAddSingleton<IWorkflowDefinitionResolver, CatalogWorkflowDefinitionResolver>();
        return services;
    }

    public static IServiceCollection AddInMemoryWorkflowDefinitionCatalog(this IServiceCollection services)
        => AddInMemoryWorkflowDefinitionCatalog(services, configure: null);

    public static IServiceCollection AddInMemoryWorkflowDefinitionCatalog(
        this IServiceCollection services,
        Action<InMemoryWorkflowDefinitionCatalogOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new InMemoryWorkflowDefinitionCatalogOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowDefinitionSeedSource, BuiltInWorkflowDefinitionSeedSource>());
        services.TryAddSingleton<InMemoryWorkflowDefinitionCatalog>(sp =>
        {
            var catalog = new InMemoryWorkflowDefinitionCatalog();
            foreach (var seedSource in sp.GetServices<IWorkflowDefinitionSeedSource>())
            {
                foreach (var (name, yaml) in seedSource.GetSeedDefinitions())
                    catalog.Upsert(name, yaml);
            }

            return catalog;
        });
        services.TryAddSingleton<IWorkflowDefinitionCatalog>(sp => sp.GetRequiredService<InMemoryWorkflowDefinitionCatalog>());
        services.TryAddSingleton<IWorkflowDefinitionLookupService>(sp => sp.GetRequiredService<InMemoryWorkflowDefinitionCatalog>());
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowDefinitionBootstrapHostedService>());
        return services;
    }
}
