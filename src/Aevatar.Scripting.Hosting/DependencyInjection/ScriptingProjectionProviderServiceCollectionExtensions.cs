using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
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

        if (services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<ScriptReadModelDocument, string>)))
            return services;

        if (configuration == null)
        {
            AddInMemoryDocumentStores(services);
            services.AddInMemoryGraphProjectionStore();
            return services;
        }

        EnsureLegacyProviderOptionsNotUsed(configuration);

        var enableElasticsearchDocument = ResolveElasticsearchDocumentEnabled(configuration);
        var enableNeo4jGraph = ResolveNeo4jGraphEnabled(configuration);
        var enableInMemoryDocument = ResolveOptionalBool(
            configuration["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !enableElasticsearchDocument);
        var enableInMemoryGraph = ResolveOptionalBool(
            configuration["Projection:Graph:Providers:InMemory:Enabled"],
            fallbackValue: !enableNeo4jGraph);

        EnforceDocumentProviderPolicy(configuration, enableInMemoryDocument);
        EnforceGraphProviderPolicy(configuration, enableInMemoryGraph);

        var documentProviderCount = (enableElasticsearchDocument ? 1 : 0) + (enableInMemoryDocument ? 1 : 0);
        if (documentProviderCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled. Configure either Projection:Document:Providers:Elasticsearch:Enabled=true or Projection:Document:Providers:InMemory:Enabled=true.");
        }

        var graphProviderCount = (enableNeo4jGraph ? 1 : 0) + (enableInMemoryGraph ? 1 : 0);
        if (graphProviderCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one graph projection provider must be enabled. Configure either Projection:Graph:Providers:Neo4j:Enabled=true or Projection:Graph:Providers:InMemory:Enabled=true.");
        }

        if (enableElasticsearchDocument)
        {
            services.AddElasticsearchDocumentProjectionStore<ScriptDefinitionSnapshotDocument, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ScriptDefinitionSnapshotDocument>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ScriptCatalogEntryDocument, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ScriptCatalogEntryDocument>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ScriptReadModelDocument, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ScriptReadModelDocument>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ScriptEvolutionReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ScriptEvolutionReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key);
            services.AddElasticsearchDocumentProjectionStore<ScriptNativeDocumentReadModel, string>(
                optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
                metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<ScriptNativeDocumentReadModel>>().Metadata,
                keySelector: readModel => readModel.Id,
                keyFormatter: key => key,
                indexScopeSelector: readModel => readModel.DocumentIndexScope);
        }
        else
        {
            AddInMemoryDocumentStores(services);
        }

        if (enableNeo4jGraph)
        {
            services.AddNeo4jGraphProjectionStore(
                optionsFactory: _ => BuildNeo4jGraphOptions(configuration));
        }
        else
        {
            services.AddInMemoryGraphProjectionStore();
        }

        return services;
    }

    private static void AddInMemoryDocumentStores(IServiceCollection services)
    {
        services.AddInMemoryDocumentProjectionStore<ScriptDefinitionSnapshotDocument, string>(
            keySelector: static readModel => readModel.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);
        services.AddInMemoryDocumentProjectionStore<ScriptCatalogEntryDocument, string>(
            keySelector: static readModel => readModel.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);
        services.AddInMemoryDocumentProjectionStore<ScriptReadModelDocument, string>(
            keySelector: static readModel => readModel.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);
        services.AddInMemoryDocumentProjectionStore<ScriptEvolutionReadModel, string>(
            keySelector: static readModel => readModel.Id,
            keyFormatter: static key => key,
            defaultSortSelector: static readModel => readModel.UpdatedAt);
        services.AddInMemoryDocumentProjectionStore<ScriptNativeDocumentReadModel, string>(
            keySelector: static readModel => readModel.Id,
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
                "Use Projection:Document:Providers:*:Enabled and Projection:Graph:Providers:*:Enabled with exactly one provider enabled per store type.");
        }
    }

    private static bool ResolveElasticsearchDocumentEnabled(IConfiguration configuration)
    {
        var section = configuration.GetSection("Projection:Document:Providers:Elasticsearch");
        var explicitEnabled = section["Enabled"];
        var hasEndpoints = section
            .GetSection("Endpoints")
            .GetChildren()
            .Select(x => x.Value?.Trim() ?? string.Empty)
            .Any(x => x.Length > 0);
        return ResolveOptionalBool(explicitEnabled, hasEndpoints);
    }

    private static bool ResolveNeo4jGraphEnabled(IConfiguration configuration)
    {
        var section = configuration.GetSection("Projection:Graph:Providers:Neo4j");
        var explicitEnabled = section["Enabled"];
        var hasUri = (section["Uri"]?.Trim().Length ?? 0) > 0;
        return ResolveOptionalBool(explicitEnabled, hasUri);
    }

    private static ElasticsearchProjectionDocumentStoreOptions BuildElasticsearchDocumentOptions(
        IConfiguration configuration)
    {
        var options = new ElasticsearchProjectionDocumentStoreOptions();
        configuration.GetSection("Projection:Document:Providers:Elasticsearch").Bind(options);
        if (options.Endpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "Projection:Document:Providers:Elasticsearch is enabled but Endpoints is empty.");
        }

        return options;
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

        return options;
    }

    private static void EnforceDocumentProviderPolicy(
        IConfiguration configuration,
        bool enableInMemoryDocumentProvider)
    {
        var denyInMemoryDocumentProvider = ResolveOptionalBool(
            configuration["Projection:Policies:DenyInMemoryDocumentReadStore"],
            fallbackValue: false);
        var environment = ResolveRuntimeEnvironment(configuration["Projection:Policies:Environment"]);
        if ((denyInMemoryDocumentProvider || IsProductionEnvironment(environment)) && enableInMemoryDocumentProvider)
        {
            throw new InvalidOperationException(
                "InMemory document provider is not allowed by projection policy. " +
                "Disable Projection:Document:Providers:InMemory:Enabled and configure Elasticsearch.");
        }
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
                "Disable Projection:Graph:Providers:InMemory:Enabled and configure Neo4j.");
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
