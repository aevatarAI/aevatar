using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Extensions.Hosting;

public static class WorkflowProjectionProviderServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(x => x.ServiceType == typeof(WorkflowProjectionProviderRegistrationsMarker)))
            return services;

        services.AddSingleton<WorkflowProjectionProviderRegistrationsMarker>();

        services.AddInMemoryReadModelStoreRegistration<WorkflowExecutionReport, string>(
            keySelector: report => report.RootActorId,
            keyFormatter: key => key,
            listSortSelector: report => report.StartedAt,
            listTakeMax: 200);
        services.AddInMemoryRelationStoreRegistration();

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
        services.AddElasticsearchRelationStoreRegistration();

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
        services.AddNeo4jRelationStoreRegistration(
            optionsFactory: _ =>
            {
                var providerOptions = new Neo4jProjectionRelationStoreOptions();
                configuration.GetSection("Projection:ReadModel:Providers:Neo4j").Bind(providerOptions);
                return providerOptions;
            },
            scope: WorkflowExecutionRelationConstants.Scope);

        return services;
    }

    private sealed class WorkflowProjectionProviderRegistrationsMarker;
}
