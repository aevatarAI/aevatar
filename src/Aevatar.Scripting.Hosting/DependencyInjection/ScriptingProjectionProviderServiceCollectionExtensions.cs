using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Stores;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Scripting.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Scripting.Hosting.DependencyInjection;

public static class ScriptingProjectionProviderServiceCollectionExtensions
{
    public static IServiceCollection AddScriptingProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration == null)
        {
            AddInMemoryDocumentStores(services);
            if (!UseExistingGraphProviderOrThrow(
                    services,
                    new ProjectionGraphProviderStatus("InMemory", Enabled: true)))
            {
                services.AddInMemoryGraphProjectionStore();
            }
            return services;
        }

        EnsureLegacyProviderOptionsNotUsed(configuration);

        var documentProvider = ProjectionDocumentProviderConfiguration.Resolve(configuration, "Scripting");
        var enableNeo4jGraph = ResolveNeo4jGraphEnabled(configuration);
        var enableInMemoryGraph = ResolveOptionalBool(
            configuration["Projection:Graph:Providers:InMemory:Enabled"],
            fallbackValue: !enableNeo4jGraph);

        EnforceGraphProviderPolicy(configuration, enableInMemoryGraph);

        var graphProviderCount = (enableNeo4jGraph ? 1 : 0) + (enableInMemoryGraph ? 1 : 0);
        if (graphProviderCount > 1)
        {
            throw new InvalidOperationException(
                "Only one graph projection provider can be enabled. Configure either Projection:Graph:Providers:Neo4j:Enabled=true or Projection:Graph:Providers:InMemory:Enabled=true.");
        }

        if (documentProvider.ElasticsearchEnabled)
        {
            TryAddElasticsearchDocumentStore<ScriptDefinitionSnapshotDocument>(
                services,
                configuration,
                static readModel => readModel.Id);
            TryAddElasticsearchDocumentStore<ScriptCatalogEntryDocument>(
                services,
                configuration,
                static readModel => readModel.Id);
            TryAddElasticsearchDocumentStore<ScriptReadModelDocument>(
                services,
                configuration,
                static readModel => readModel.Id);
            TryAddElasticsearchDocumentStore<ScriptEvolutionReadModel>(
                services,
                configuration,
                static readModel => readModel.Id);
            TryAddElasticsearchDocumentStore<ScriptNativeDocumentReadModel>(
                services,
                configuration,
                static readModel => readModel.Id,
                static readModel => readModel.DocumentIndexScope);
        }
        else
        {
            AddInMemoryDocumentStores(services);
        }

        var expectedGraphProvider = enableNeo4jGraph
            ? new ProjectionGraphProviderStatus("Neo4j", Enabled: true)
            : enableInMemoryGraph
                ? new ProjectionGraphProviderStatus("InMemory", Enabled: true)
                : new ProjectionGraphProviderStatus("Disabled", Enabled: false);
        if (UseExistingGraphProviderOrThrow(services, expectedGraphProvider))
            return services;

        if (enableNeo4jGraph)
        {
            services.AddNeo4jGraphProjectionStore(
                optionsFactory: _ => BuildNeo4jGraphOptions(configuration));
        }
        else if (enableInMemoryGraph)
        {
            services.AddInMemoryGraphProjectionStore();
        }
        else
        {
            services.AddDisabledGraphProjectionStore();
        }

        return services;
    }

    private static void AddInMemoryDocumentStores(IServiceCollection services)
    {
        TryAddInMemoryDocumentStore<ScriptDefinitionSnapshotDocument>(services, static readModel => readModel.Id);
        TryAddInMemoryDocumentStore<ScriptCatalogEntryDocument>(services, static readModel => readModel.Id);
        TryAddInMemoryDocumentStore<ScriptReadModelDocument>(services, static readModel => readModel.Id);
        TryAddInMemoryDocumentStore<ScriptEvolutionReadModel>(services, static readModel => readModel.Id);
        TryAddInMemoryDocumentStore<ScriptNativeDocumentReadModel>(services, static readModel => readModel.Id);
    }

    private static bool HasAnyDocumentReader<TDocument>(IServiceCollection services)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        return services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<TDocument, string>));
    }

    private static bool HasDocumentReaderForProvider<TDocument>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        return providerKind switch
        {
            ProjectionDocumentProviderKind.Elasticsearch => services.Any(x => x.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TDocument, string>)),
            ProjectionDocumentProviderKind.InMemory => services.Any(x => x.ServiceType == typeof(InMemoryProjectionDocumentStore<TDocument, string>)),
            _ => false,
        };
    }

    private static void EnsureCompatibleDocumentReaderProvider<TDocument>(
        IServiceCollection services,
        ProjectionDocumentProviderKind providerKind)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        if (!HasAnyDocumentReader<TDocument>(services))
            return;
        if (HasDocumentReaderForProvider<TDocument>(services, providerKind))
            return;

        throw new InvalidOperationException(
            $"Projection document reader for {typeof(TDocument).Name} is already registered with a different provider.");
    }

    private static void TryAddElasticsearchDocumentStore<TDocument>(
        IServiceCollection services,
        IConfiguration configuration,
        Func<TDocument, string> keySelector,
        Func<TDocument, string?>? indexScopeSelector = null)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TDocument>(services, ProjectionDocumentProviderKind.Elasticsearch);
        if (HasDocumentReaderForProvider<TDocument>(services, ProjectionDocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TDocument, string>(
            optionsFactory: _ => ProjectionDocumentProviderConfiguration.BindRequiredElasticsearchOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TDocument>>().Metadata,
            keySelector: keySelector,
            keyFormatter: static key => key,
            indexScopeSelector: indexScopeSelector);
    }

    private static void TryAddInMemoryDocumentStore<TDocument>(
        IServiceCollection services,
        Func<TDocument, string> keySelector)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        EnsureCompatibleDocumentReaderProvider<TDocument>(services, ProjectionDocumentProviderKind.InMemory);
        if (HasDocumentReaderForProvider<TDocument>(services, ProjectionDocumentProviderKind.InMemory))
            return;

        services.AddInMemoryDocumentProjectionStore<TDocument, string>(
            keySelector: keySelector,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);
    }

    private static void EnsureLegacyProviderOptionsNotUsed(IConfiguration configuration)
    {
        var legacyDocumentProvider = configuration["Projection:Document:Provider"]?.Trim();
        var legacyGraphProvider = configuration["Projection:Graph:Provider"]?.Trim();
        if (legacyDocumentProvider?.Length > 0 || legacyGraphProvider?.Length > 0)
        {
            throw new InvalidOperationException(
                "Legacy provider single-selection options are no longer supported. " +
                "Use Projection:Document:Providers:*:Enabled with exactly one document provider and " +
                "Projection:Graph:Providers:*:Enabled with at most one graph provider.");
        }
    }

    private static bool ResolveNeo4jGraphEnabled(IConfiguration configuration)
    {
        var section = configuration.GetSection("Projection:Graph:Providers:Neo4j");
        return ResolveOptionalBool(section["Enabled"], fallbackValue: false);
    }

    private static bool UseExistingGraphProviderOrThrow(
        IServiceCollection services,
        ProjectionGraphProviderStatus expectedStatus)
    {
        if (!services.Any(x => x.ServiceType == typeof(IProjectionGraphStore)))
            return false;

        var statusDescriptors = services
            .Where(x => x.ServiceType == typeof(ProjectionGraphProviderStatus))
            .ToArray();
        var registeredStatus = statusDescriptors.Length == 1
            ? statusDescriptors[0].ImplementationInstance as ProjectionGraphProviderStatus
            : null;
        var hasVersionedStore = services.Any(x => x.ServiceType == typeof(IVersionedProjectionGraphStore));
        if (!hasVersionedStore || registeredStatus != expectedStatus)
        {
            throw new InvalidOperationException(
                $"The existing graph projection provider registration is incompatible with the configured " +
                $"'{expectedStatus.ProviderName}' provider. Register IProjectionGraphStore, " +
                "IVersionedProjectionGraphStore, and ProjectionGraphProviderStatus as one matching provider.");
        }

        return true;
    }

    private static Neo4jProjectionGraphStoreOptions BuildNeo4jGraphOptions(
        IConfiguration configuration)
    {
        var options = new Neo4jProjectionGraphStoreOptions();
        configuration.GetSection("Projection:Graph:Providers:Neo4j").Bind(options);
        if (string.IsNullOrWhiteSpace(options.Uri))
        {
            throw new InvalidOperationException(
                "Projection:Graph:Providers:Neo4j is enabled but Uri is empty.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException(
                "Projection:Graph:Providers:Neo4j is enabled but Password is empty. " +
                "Inject it via environment variable AEVATAR_Projection__Graph__Providers__Neo4j__Password.");
        }

        return options;
    }

    private static void EnforceGraphProviderPolicy(
        IConfiguration configuration,
        bool enableInMemoryGraphProvider)
    {
        var denyInMemoryGraphProvider = ResolveOptionalBool(
            configuration["Projection:Policies:DenyInMemoryGraphFactStore"],
            fallbackValue: false);
        var environment = ResolveRuntimeEnvironment(configuration["Projection:Policies:Environment"]);
        if ((denyInMemoryGraphProvider || IsProductionEnvironment(environment)) && enableInMemoryGraphProvider)
        {
            throw new InvalidOperationException(
                "InMemory graph provider is not allowed by projection policy. " +
                "Disable Projection:Graph:Providers:InMemory:Enabled and either configure Neo4j or disable graph projection.");
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
        return aspnetEnvironment?.Trim() ?? string.Empty;
    }

    private static bool IsProductionEnvironment(string environment) =>
        string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);

    private static bool ResolveOptionalBool(string? rawValue, bool fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallbackValue;

        if (!bool.TryParse(rawValue, out var parsed))
            throw new InvalidOperationException($"Invalid boolean value '{rawValue}'.");

        return parsed;
    }

}
