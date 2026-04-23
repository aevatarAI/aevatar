using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
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
            if (HasAllScriptingDocumentReaders(services, DocumentProviderKind.InMemory))
                return services;

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

        var selectedDocumentProvider = enableElasticsearchDocument
            ? DocumentProviderKind.Elasticsearch
            : DocumentProviderKind.InMemory;
        if (HasAllScriptingDocumentReaders(services, selectedDocumentProvider))
            return services;

        var graphProviderCount = (enableNeo4jGraph ? 1 : 0) + (enableInMemoryGraph ? 1 : 0);
        if (graphProviderCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one graph projection provider must be enabled. Configure either Projection:Graph:Providers:Neo4j:Enabled=true or Projection:Graph:Providers:InMemory:Enabled=true.");
        }

        if (enableElasticsearchDocument)
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
        TryAddInMemoryDocumentStore<ScriptDefinitionSnapshotDocument>(services, static readModel => readModel.Id);
        TryAddInMemoryDocumentStore<ScriptCatalogEntryDocument>(services, static readModel => readModel.Id);
        TryAddInMemoryDocumentStore<ScriptReadModelDocument>(services, static readModel => readModel.Id);
        TryAddInMemoryDocumentStore<ScriptEvolutionReadModel>(services, static readModel => readModel.Id);
        TryAddInMemoryDocumentStore<ScriptNativeDocumentReadModel>(services, static readModel => readModel.Id);
    }

    private static bool HasAllScriptingDocumentReaders(
        IServiceCollection services,
        DocumentProviderKind providerKind)
    {
        return HasDocumentReaderForProvider<ScriptDefinitionSnapshotDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ScriptCatalogEntryDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ScriptReadModelDocument>(services, providerKind)
               && HasDocumentReaderForProvider<ScriptEvolutionReadModel>(services, providerKind)
               && HasDocumentReaderForProvider<ScriptNativeDocumentReadModel>(services, providerKind);
    }

    private static bool HasAnyDocumentReader<TDocument>(IServiceCollection services)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        return services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<TDocument, string>));
    }

    private static bool HasDocumentReaderForProvider<TDocument>(
        IServiceCollection services,
        DocumentProviderKind providerKind)
        where TDocument : class, IProjectionReadModel<TDocument>, new()
    {
        return providerKind switch
        {
            DocumentProviderKind.Elasticsearch => services.Any(x => x.ServiceType == typeof(ElasticsearchProjectionDocumentStore<TDocument, string>)),
            DocumentProviderKind.InMemory => services.Any(x => x.ServiceType == typeof(InMemoryProjectionDocumentStore<TDocument, string>)),
            _ => false,
        };
    }

    private static void EnsureCompatibleDocumentReaderProvider<TDocument>(
        IServiceCollection services,
        DocumentProviderKind providerKind)
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
        EnsureCompatibleDocumentReaderProvider<TDocument>(services, DocumentProviderKind.Elasticsearch);
        if (HasDocumentReaderForProvider<TDocument>(services, DocumentProviderKind.Elasticsearch))
            return;

        services.AddElasticsearchDocumentProjectionStore<TDocument, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
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
        EnsureCompatibleDocumentReaderProvider<TDocument>(services, DocumentProviderKind.InMemory);
        if (HasDocumentReaderForProvider<TDocument>(services, DocumentProviderKind.InMemory))
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

    private enum DocumentProviderKind
    {
        InMemory,
        Elasticsearch,
    }
}
