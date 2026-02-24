using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        var runtimeOptions = ResolveRuntimeOptions(configuration);
        var providerSelection = ResolveProviderSelection(runtimeOptions);
        EnforceRelationProviderPolicy(configuration, providerSelection.RelationProvider);

        services.Replace(ServiceDescriptor.Singleton(runtimeOptions));
        services.Replace(ServiceDescriptor.Singleton<IProjectionStoreSelectionRuntimeOptions>(sp =>
            sp.GetRequiredService<ProjectionReadModelRuntimeOptions>()));

        RegisterReadModelProvider(services, configuration, providerSelection.ReadModelProvider);
        RegisterRelationProvider(services, configuration, providerSelection.RelationProvider);

        return services;
    }

    private static ProjectionReadModelRuntimeOptions ResolveRuntimeOptions(IConfiguration configuration)
    {
        var options = new ProjectionReadModelRuntimeOptions();
        configuration.GetSection("Projection:ReadModel").Bind(options);
        return options;
    }

    private static ProviderSelection ResolveProviderSelection(ProjectionReadModelRuntimeOptions runtimeOptions)
    {
        var readModelProvider = NormalizeOrDefaultProvider(
            runtimeOptions.Provider,
            ProjectionReadModelProviderNames.InMemory,
            "Projection:ReadModel:Provider");

        var relationProvider = NormalizeOrDefaultProvider(
            runtimeOptions.RelationProvider,
            readModelProvider,
            "Projection:ReadModel:RelationProvider");

        return new ProviderSelection(readModelProvider, relationProvider);
    }

    private static void EnforceRelationProviderPolicy(
        IConfiguration configuration,
        string relationProviderName)
    {
        var denyInMemoryRelationProvider = ParseBool(
            configuration["Projection:Policies:DenyInMemoryRelationFactStore"]);
        var environment = ResolveRuntimeEnvironment(configuration["Projection:Policies:Environment"]);
        var production = IsProductionEnvironment(environment);

        if ((denyInMemoryRelationProvider || production) &&
            string.Equals(
                relationProviderName,
                ProjectionReadModelProviderNames.InMemory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "InMemory relation provider is not allowed by projection policy. " +
                "Use a durable relation provider (for example Neo4j) for production/distributed deployments.");
        }
    }

    private static string ResolveRuntimeEnvironment(string? configuredEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(configuredEnvironment))
            return configuredEnvironment.Trim();

        var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(dotnetEnvironment))
            return dotnetEnvironment.Trim();

        var aspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return aspnetEnvironment?.Trim() ?? "";
    }

    private static bool IsProductionEnvironment(string environment)
    {
        return string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ParseBool(string? value)
    {
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static string NormalizeOrDefaultProvider(
        string? configuredValue,
        string fallbackValue,
        string optionPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredValue)
            ? fallbackValue
            : configuredValue.Trim();

        if (string.Equals(candidate, ProjectionReadModelProviderNames.InMemory, StringComparison.OrdinalIgnoreCase))
            return ProjectionReadModelProviderNames.InMemory;
        if (string.Equals(candidate, ProjectionReadModelProviderNames.Elasticsearch, StringComparison.OrdinalIgnoreCase))
            return ProjectionReadModelProviderNames.Elasticsearch;
        if (string.Equals(candidate, ProjectionReadModelProviderNames.Neo4j, StringComparison.OrdinalIgnoreCase))
            return ProjectionReadModelProviderNames.Neo4j;

        throw new InvalidOperationException(
            $"Unsupported projection provider '{candidate}' configured at '{optionPath}'. " +
            $"Allowed values: {ProjectionReadModelProviderNames.InMemory}, {ProjectionReadModelProviderNames.Elasticsearch}, {ProjectionReadModelProviderNames.Neo4j}.");
    }

    private static void RegisterReadModelProvider(
        IServiceCollection services,
        IConfiguration configuration,
        string providerName)
    {
        switch (providerName)
        {
            case ProjectionReadModelProviderNames.InMemory:
                services.AddInMemoryReadModelStoreRegistration<WorkflowExecutionReport, string>(
                    keySelector: report => report.RootActorId,
                    keyFormatter: key => key,
                    listSortSelector: report => report.CreatedAt,
                    listTakeMax: 200);
                break;
            case ProjectionReadModelProviderNames.Elasticsearch:
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
                break;
            case ProjectionReadModelProviderNames.Neo4j:
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
                break;
            default:
                throw new InvalidOperationException($"Unsupported read-model provider '{providerName}'.");
        }
    }

    private static void RegisterRelationProvider(
        IServiceCollection services,
        IConfiguration configuration,
        string providerName)
    {
        switch (providerName)
        {
            case ProjectionReadModelProviderNames.InMemory:
                services.AddInMemoryRelationStoreRegistration();
                break;
            case ProjectionReadModelProviderNames.Elasticsearch:
                services.AddElasticsearchRelationStoreRegistration();
                break;
            case ProjectionReadModelProviderNames.Neo4j:
                services.AddNeo4jRelationStoreRegistration(
                    optionsFactory: _ =>
                    {
                        var providerOptions = new Neo4jProjectionRelationStoreOptions();
                        configuration.GetSection("Projection:ReadModel:Providers:Neo4j").Bind(providerOptions);
                        return providerOptions;
                    },
                    scope: WorkflowExecutionRelationConstants.Scope);
                break;
            default:
                throw new InvalidOperationException($"Unsupported relation provider '{providerName}'.");
        }
    }

    private sealed class WorkflowProjectionProviderRegistrationsMarker;

    private sealed record ProviderSelection(string ReadModelProvider, string RelationProvider);
}
