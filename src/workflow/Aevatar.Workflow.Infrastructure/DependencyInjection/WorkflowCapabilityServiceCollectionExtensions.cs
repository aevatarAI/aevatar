using Aevatar.Configuration;
using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.Workflow.Application.DependencyInjection;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Presentation.AGUIAdapter;
using Aevatar.Workflow.Presentation.AGUIAdapter.DependencyInjection;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.ReadModels;
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
        services.AddInMemoryReadModelStoreRegistration<WorkflowExecutionReport, string>(
            keySelector: report => report.RootActorId,
            keyFormatter: key => key,
            listSortSelector: report => report.StartedAt,
            listTakeMax: 200);
        services.AddElasticsearchReadModelStoreRegistration<WorkflowExecutionReport, string>(
            optionsFactory: _ =>
            {
                var providerOptions = new ElasticsearchProjectionReadModelStoreOptions();
                configuration.GetSection("Projection:ReadModel:Providers:Elasticsearch").Bind(providerOptions);
                return providerOptions;
            },
            indexScope: "workflow-execution-reports",
            keySelector: report => report.RootActorId,
            keyFormatter: key => key);
        services.AddNeo4jReadModelStoreRegistration<WorkflowExecutionReport, string>(
            optionsFactory: _ =>
            {
                var providerOptions = new Neo4jProjectionReadModelStoreOptions();
                configuration.GetSection("Projection:ReadModel:Providers:Neo4j").Bind(providerOptions);
                return providerOptions;
            },
            scope: "workflow-execution-reports",
            keySelector: report => report.RootActorId,
            keyFormatter: key => key);
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
        var readModelOptions = new ProjectionReadModelRuntimeOptions();
        configuration.GetSection("Projection:ReadModel").Bind(readModelOptions);

        if (!string.IsNullOrWhiteSpace(readModelOptions.Provider))
            options.ReadModelProvider = readModelOptions.Provider.Trim();

        options.ReadModelMode = readModelOptions.Mode;
        options.FailOnUnsupportedCapabilities = readModelOptions.FailOnUnsupportedCapabilities;
        options.ReadModelBindings.Clear();
        foreach (var item in readModelOptions.Bindings)
            options.ReadModelBindings[item.Key] = item.Value;
    }
}
