using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.Neo4j.Configuration;
using Aevatar.CQRS.Projection.Providers.Neo4j.DependencyInjection;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.DynamicRuntime.Abstractions.Contracts;
using Aevatar.DynamicRuntime.Projection.ReadModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.DynamicRuntime.Projection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDynamicRuntimeProjection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(x => x.ServiceType == typeof(DynamicRuntimeProjectionProviderRegistrationsMarker)))
            return services;

        EnsureLegacyProviderOptionsNotUsed(configuration);

        services.AddSingleton<DynamicRuntimeProjectionProviderRegistrationsMarker>();
        services.AddProjectionReadModelRuntime();

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
            AddElasticsearchDocumentStores(services, configuration);
        else
            AddInMemoryDocumentStores(services);

        if (enableNeo4jGraph)
        {
            services.AddNeo4jGraphProjectionStore(
                optionsFactory: _ => BuildNeo4jGraphOptions(configuration));
        }
        else
        {
            services.AddInMemoryGraphProjectionStore();
        }

        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeImageReadModel>, DynamicRuntimeImageReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeStackReadModel>, DynamicRuntimeStackReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeComposeServiceReadModel>, DynamicRuntimeComposeServiceReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeComposeEventReadModel>, DynamicRuntimeComposeEventReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeServiceDefinitionReadModel>, DynamicRuntimeServiceDefinitionReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeContainerReadModel>, DynamicRuntimeContainerReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeRunReadModel>, DynamicRuntimeRunReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeBuildJobReadModel>, DynamicRuntimeBuildJobReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeScriptReadModelDefinitionReadModel>, DynamicRuntimeScriptReadModelDefinitionReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeScriptReadModelRelationReadModel>, DynamicRuntimeScriptReadModelRelationReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<DynamicRuntimeScriptReadModelDocumentReadModel>, DynamicRuntimeScriptReadModelDocumentReadModelMetadataProvider>();
        services.TryAddSingleton<IDynamicRuntimeReadStore, ProjectionBackedDynamicRuntimeReadStore>();

        return services;
    }

    private static void AddInMemoryDocumentStores(IServiceCollection services)
    {
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeImageReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeStackReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeComposeServiceReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeComposeEventReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeServiceDefinitionReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeContainerReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeRunReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeBuildJobReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeScriptReadModelDefinitionReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeScriptReadModelRelationReadModel, string>(model => model.Id);
        services.AddInMemoryDocumentProjectionStore<DynamicRuntimeScriptReadModelDocumentReadModel, string>(model => model.Id);
    }

    private static void AddElasticsearchDocumentStores(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeImageReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeImageReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeStackReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeStackReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeComposeServiceReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeComposeServiceReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeComposeEventReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeComposeEventReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeServiceDefinitionReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeServiceDefinitionReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeContainerReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeContainerReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeRunReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeRunReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeBuildJobReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeBuildJobReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeScriptReadModelDefinitionReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeScriptReadModelDefinitionReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeScriptReadModelRelationReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeScriptReadModelRelationReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
        services.AddElasticsearchDocumentProjectionStore<DynamicRuntimeScriptReadModelDocumentReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataResolver>().Resolve<DynamicRuntimeScriptReadModelDocumentReadModel>(),
            keySelector: model => model.Id,
            keyFormatter: key => key);
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
            .Select(x => x.Value?.Trim() ?? "")
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

    private static ElasticsearchProjectionDocumentStoreOptions BuildElasticsearchDocumentOptions(IConfiguration configuration)
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

    private static Neo4jProjectionGraphStoreOptions BuildNeo4jGraphOptions(IConfiguration configuration)
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
        var production = IsProductionEnvironment(environment);

        if ((denyInMemoryDocumentProvider || production) && enableInMemoryDocumentProvider)
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
        var production = IsProductionEnvironment(environment);

        if ((denyInMemoryGraphProvider || production) && enableInMemoryGraphProvider)
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
        return aspnetEnvironment?.Trim() ?? "";
    }

    private static bool IsProductionEnvironment(string environment)
    {
        return string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ResolveOptionalBool(string? rawValue, bool fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallbackValue;

        if (!bool.TryParse(rawValue, out var parsed))
            throw new InvalidOperationException($"Invalid boolean value '{rawValue}'.");

        return parsed;
    }

    private sealed class DynamicRuntimeProjectionProviderRegistrationsMarker;
}
