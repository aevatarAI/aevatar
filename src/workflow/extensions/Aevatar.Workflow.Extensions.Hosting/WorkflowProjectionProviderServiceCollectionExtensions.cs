using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
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

        var providerSelection = ResolveProviderSelection(configuration);
        EnforceGraphProviderPolicy(configuration, providerSelection.GraphProvider);
        var documentRuntimeOptions = new ProjectionDocumentRuntimeOptions
        {
            ProviderName = providerSelection.DocumentProvider,
            FailFastOnStartup = true,
        };
        var graphRuntimeOptions = new ProjectionGraphRuntimeOptions
        {
            ProviderName = providerSelection.GraphProvider,
            FailFastOnStartup = true,
        };

        services.Replace(ServiceDescriptor.Singleton(documentRuntimeOptions));
        services.Replace(ServiceDescriptor.Singleton<IProjectionDocumentRuntimeOptions>(sp =>
            sp.GetRequiredService<ProjectionDocumentRuntimeOptions>()));
        services.Replace(ServiceDescriptor.Singleton(graphRuntimeOptions));
        services.Replace(ServiceDescriptor.Singleton<IProjectionGraphRuntimeOptions>(sp =>
            sp.GetRequiredService<ProjectionGraphRuntimeOptions>()));

        RegisterDocumentProvider(services, configuration, providerSelection.DocumentProvider);
        RegisterGraphProvider(services, configuration, providerSelection.GraphProvider);

        return services;
    }

    private static ProviderSelection ResolveProviderSelection(IConfiguration configuration)
    {
        var documentProvider = NormalizeOrDefaultProvider(
            configuration["Projection:Document:Provider"],
            ProjectionProviderNames.InMemory,
            "Projection:Document:Provider");

        var graphProvider = NormalizeOrDefaultProvider(
            configuration["Projection:Graph:Provider"],
            documentProvider,
            "Projection:Graph:Provider");

        return new ProviderSelection(documentProvider, graphProvider);
    }

    private static void EnforceGraphProviderPolicy(
        IConfiguration configuration,
        string graphProviderName)
    {
        var denyInMemoryGraphProvider = ParseBool(
            configuration["Projection:Policies:DenyInMemoryGraphFactStore"]);
        var environment = ResolveRuntimeEnvironment(configuration["Projection:Policies:Environment"]);
        var production = IsProductionEnvironment(environment);

        if ((denyInMemoryGraphProvider || production) &&
            string.Equals(
                graphProviderName,
                ProjectionProviderNames.InMemory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "InMemory graph provider is not allowed by projection policy. " +
                "Use a durable graph provider (for example Neo4j) for production/distributed deployments.");
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

        if (string.Equals(candidate, ProjectionProviderNames.InMemory, StringComparison.OrdinalIgnoreCase))
            return ProjectionProviderNames.InMemory;
        if (string.Equals(candidate, ProjectionProviderNames.Elasticsearch, StringComparison.OrdinalIgnoreCase))
            return ProjectionProviderNames.Elasticsearch;
        if (string.Equals(candidate, ProjectionProviderNames.Neo4j, StringComparison.OrdinalIgnoreCase))
            return ProjectionProviderNames.Neo4j;

        throw new InvalidOperationException(
            $"Unsupported projection provider '{candidate}' configured at '{optionPath}'. " +
            $"Allowed values: {ProjectionProviderNames.InMemory}, {ProjectionProviderNames.Elasticsearch}, {ProjectionProviderNames.Neo4j}.");
    }

    private static void RegisterDocumentProvider(
        IServiceCollection services,
        IConfiguration configuration,
        string providerName)
    {
        switch (providerName)
        {
            case ProjectionProviderNames.InMemory:
                services.AddInMemoryDocumentStoreRegistration<WorkflowExecutionReport, string>(
                    keySelector: report => report.RootActorId,
                    keyFormatter: key => key,
                    listSortSelector: report => report.CreatedAt,
                    listTakeMax: 200);
                break;
            case ProjectionProviderNames.Elasticsearch:
                services.AddElasticsearchDocumentStoreRegistration<WorkflowExecutionReport, string>(
                    optionsFactory: _ =>
                    {
                        var providerOptions = new ElasticsearchProjectionReadModelStoreOptions();
                        configuration.GetSection("Projection:Document:Providers:Elasticsearch").Bind(providerOptions);
                        return providerOptions;
                    },
                    indexScopeFactory: sp =>
                    {
                        var metadataResolver = sp.GetRequiredService<IProjectionDocumentMetadataResolver>();
                        return metadataResolver.Resolve<WorkflowExecutionReport>().IndexName;
                    },
                    keySelector: report => report.RootActorId,
                    keyFormatter: key => key);
                break;
            case ProjectionProviderNames.Neo4j:
                services.AddNeo4jDocumentStoreRegistration<WorkflowExecutionReport, string>(
                    optionsFactory: _ =>
                    {
                        var providerOptions = new Neo4jProjectionReadModelStoreOptions();
                        configuration.GetSection("Projection:Document:Providers:Neo4j").Bind(providerOptions);
                        return providerOptions;
                    },
                    scopeFactory: sp =>
                    {
                        var metadataResolver = sp.GetRequiredService<IProjectionDocumentMetadataResolver>();
                        return metadataResolver.Resolve<WorkflowExecutionReport>().IndexName;
                    },
                    keySelector: report => report.RootActorId,
                    keyFormatter: key => key);
                break;
            default:
                throw new InvalidOperationException($"Unsupported document provider '{providerName}'.");
        }
    }

    private static void RegisterGraphProvider(
        IServiceCollection services,
        IConfiguration configuration,
        string providerName)
    {
        switch (providerName)
        {
            case ProjectionProviderNames.InMemory:
                services.AddInMemoryGraphStoreRegistration();
                break;
            case ProjectionProviderNames.Elasticsearch:
                throw new InvalidOperationException(
                    "Elasticsearch cannot be used as graph provider. Use InMemory (dev/test) or Neo4j.");
            case ProjectionProviderNames.Neo4j:
                services.AddNeo4jGraphStoreRegistration(
                    optionsFactory: _ =>
                    {
                        var providerOptions = new Neo4jProjectionGraphStoreOptions();
                        configuration.GetSection("Projection:Graph:Providers:Neo4j").Bind(providerOptions);
                        return providerOptions;
                    },
                    scopeFactory: _ => WorkflowExecutionGraphConstants.Scope);
                break;
            default:
                throw new InvalidOperationException($"Unsupported graph provider '{providerName}'.");
        }
    }

    private sealed class WorkflowProjectionProviderRegistrationsMarker;

    private sealed record ProviderSelection(string DocumentProvider, string GraphProvider);
}
