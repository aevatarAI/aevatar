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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.Workflow.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowInfrastructure(
        this IServiceCollection services,
        Action<WorkflowRunReportExportOptions>? configureReportExport = null,
        IConfiguration? configuration = null)
    {
        services.AddOptions<WorkflowRunReportExportOptions>();
        if (configureReportExport != null)
            services.Configure(configureReportExport);
        services.AddOptions<FileSystemWorkflowFileIngressOptions>();
        var artifactOptions = services.AddOptions<WorkflowFileArtifactOptions>();
        if (configuration != null)
        {
            artifactOptions.Bind(configuration.GetSection(WorkflowFileArtifactOptions.SectionName));
        }

        ConfigureWorkflowFileArtifacts(services, configuration);
        var submitOptions = services.AddOptions<WorkflowConnectedServiceFileSubmitOptions>();
        if (configuration != null)
        {
            submitOptions.Bind(
                configuration.GetSection(WorkflowConnectedServiceFileSubmitOptions.SectionName));
        }

        submitOptions.ValidateOnStart();
        var spreadsheetOptions = services.AddOptions<WorkflowSpreadsheetExtractOptions>();
        if (configuration != null)
        {
            spreadsheetOptions.Bind(
                configuration.GetSection(WorkflowSpreadsheetExtractOptions.SectionName));
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<WorkflowConnectedServiceFileSubmitOptions>,
            WorkflowConnectedServiceFileSubmitOptionsValidator>());

        // Replace the Noop fallback from Application layer with the real file export adapter.
        services.Replace(ServiceDescriptor.Singleton<IWorkflowRunReportExportPort, FileSystemWorkflowRunReportExporter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowFileArtifactCleanupHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowToolSource, WorkflowDocumentExtractToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowToolSource, WorkflowSpreadsheetExtractToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowToolSource, WorkflowConnectedServiceResourceFetchToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowToolSource, WorkflowFileSubmitToolSource>());
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

    private static void ConfigureWorkflowFileArtifacts(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        var backend = ResolveWorkflowFileArtifactBackend(configuration);
        if (string.Equals(backend, "External", StringComparison.OrdinalIgnoreCase))
        {
            EnsureWorkflowFileArtifactPortRegistered<IWorkflowFileIngressPort>(services);
            EnsureWorkflowFileArtifactPortRegistered<IWorkflowFileArtifactReadPort>(services);
            EnsureWorkflowFileArtifactPortRegistered<IWorkflowFileArtifactOwnershipPort>(services);
            EnsureWorkflowFileArtifactPortRegistered<IWorkflowFileArtifactCleanupPort>(services);
            return;
        }

        if (!string.Equals(backend, "FileSystem", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "WorkflowFileArtifacts:Backend must be either FileSystem or External.");
        }

        services.TryAddSingleton<FileSystemWorkflowFileIngressPort>();
        services.TryAddSingleton<IWorkflowFileIngressPort>(sp =>
            sp.GetRequiredService<FileSystemWorkflowFileIngressPort>());
        services.TryAddSingleton<IWorkflowFileArtifactReadPort>(sp =>
            sp.GetRequiredService<FileSystemWorkflowFileIngressPort>());
        services.TryAddSingleton<IWorkflowFileArtifactOwnershipPort>(sp =>
            sp.GetRequiredService<FileSystemWorkflowFileIngressPort>());
        services.TryAddSingleton<IWorkflowFileArtifactCleanupPort>(sp =>
            sp.GetRequiredService<FileSystemWorkflowFileIngressPort>());
    }

    private static string ResolveWorkflowFileArtifactBackend(IConfiguration? configuration)
    {
        var configuredBackend = configuration?[WorkflowFileArtifactOptions.SectionName + ":Backend"]?.Trim();
        var environment = configuration?[WorkflowFileArtifactOptions.SectionName + ":Policies:Environment"]?.Trim();
        environment = string.IsNullOrWhiteSpace(environment)
            ? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            : environment;
        environment = string.IsNullOrWhiteSpace(environment)
            ? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            : environment;

        if (string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(configuredBackend, "External", StringComparison.OrdinalIgnoreCase))
                return "External";

            throw new InvalidOperationException(
                "WorkflowFileArtifacts:Backend must be External in Production and all workflow file artifact ports must be explicitly registered.");
        }

        if (!string.IsNullOrWhiteSpace(configuredBackend))
            return configuredBackend;

        return "FileSystem";
    }

    private static void EnsureWorkflowFileArtifactPortRegistered<TPort>(IServiceCollection services)
    {
        if (services.Any(static descriptor => descriptor.ServiceType == typeof(TPort)))
            return;

        throw new InvalidOperationException(
            $"WorkflowFileArtifacts:Backend=External requires an explicit {typeof(TPort).Name} registration.");
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
