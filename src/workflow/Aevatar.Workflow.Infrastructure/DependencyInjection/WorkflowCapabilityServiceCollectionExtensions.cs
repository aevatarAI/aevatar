using Aevatar.Configuration;
using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Presentation.AGUIAdapter.DependencyInjection;
using Aevatar.Workflow.Projection.Configuration;
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
        {
            configuration.GetSection("WorkflowExecutionProjection").Bind(options);
            ApplyGlobalReadModelOptions(configuration, options);
        });
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

    private static void ApplyGlobalReadModelOptions(
        IConfiguration configuration,
        WorkflowExecutionProjectionOptions options)
    {
        var readModelSection = configuration.GetSection("Projection:ReadModel");
        if (!readModelSection.Exists())
            return;

        var readModelOptions = new ProjectionReadModelRuntimeOptions();
        readModelSection.Bind(readModelOptions);

        var configuredProvider = readModelSection["Provider"];
        if (!string.IsNullOrWhiteSpace(configuredProvider))
            options.ReadModelProvider = configuredProvider.Trim();
        var configuredRelationProvider = readModelSection["RelationProvider"];
        if (!string.IsNullOrWhiteSpace(configuredRelationProvider))
            options.RelationProvider = configuredRelationProvider.Trim();

        if (!string.IsNullOrWhiteSpace(readModelSection["Mode"]))
            options.ReadModelMode = readModelOptions.Mode;
        if (!string.IsNullOrWhiteSpace(readModelSection["FailOnUnsupportedCapabilities"]))
            options.FailOnUnsupportedCapabilities = readModelOptions.FailOnUnsupportedCapabilities;

        var bindingsSection = readModelSection.GetSection("Bindings");
        if (bindingsSection.Exists())
        {
            options.ReadModelBindings.Clear();
            foreach (var item in readModelOptions.Bindings)
                options.ReadModelBindings[item.Key] = item.Value;
        }
    }
}
