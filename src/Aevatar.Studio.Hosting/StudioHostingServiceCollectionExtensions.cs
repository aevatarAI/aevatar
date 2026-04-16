using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.Configuration;
using Aevatar.CQRS.Projection.Providers.Elasticsearch.DependencyInjection;
using Aevatar.CQRS.Projection.Providers.InMemory.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.GAgents.ConnectorCatalog;
using Aevatar.GAgents.Registry;
using Aevatar.GAgents.RoleCatalog;
using Aevatar.GAgents.StreamingProxyParticipant;
using Aevatar.GAgents.UserConfig;
using Aevatar.GAgents.UserMemory;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using Aevatar.Studio.Infrastructure.ScopeResolution; // DefaultAppScopeResolver
using Aevatar.Studio.Projection.DependencyInjection;
using Aevatar.Studio.Projection.ReadModels;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Hosting;

internal static class StudioHostingServiceCollectionExtensions
{
    internal static IServiceCollection AddStudioHostingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(EditorController).Assembly)
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                json.JsonSerializerOptions.DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            });
        services.AddHttpContextAccessor();
        services.AddSingleton<IAppScopeResolver, DefaultAppScopeResolver>();
        services.AddStudioApplication();
        services.AddStudioInfrastructure(configuration);
        services.AddStudioProjectionComponents();
        services.AddStudioProjectionReadModelProviders(configuration);
        return services;
    }

    internal static IServiceCollection AddStudioBridgeServices(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AppScopedWorkflowService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IWorkflowYamlDocumentService>(),
            sp.GetService<IScopeWorkflowQueryPort>(),
            sp.GetService<IScopeWorkflowCommandPort>(),
            sp.GetService<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowActorBindingReader>(),
            sp.GetService<Aevatar.GAgentService.Abstractions.Ports.IServiceRevisionArtifactStore>(),
            sp.GetService<Aevatar.GAgentService.Abstractions.Ports.IServiceLifecycleQueryPort>(),
            sp.GetService<IWorkflowStoragePort>()));
        services.AddSingleton(sp => new AppScopedScriptService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetService<IScopeScriptQueryPort>(),
            sp.GetService<IScopeScriptCommandPort>(),
            sp.GetService<IScopeScriptSaveObservationPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptDefinitionSnapshotPort>(),
            sp.GetService<Aevatar.Scripting.Application.IScriptEvolutionApplicationService>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptCatalogQueryPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptEvolutionDecisionReadPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptingActorAddressResolver>(),
            sp.GetService<Aevatar.Scripting.Application.Queries.IScriptReadModelQueryApplicationService>(),
            sp.GetService<IScriptStoragePort>()));
        return services;
    }

    internal static IServiceCollection AddStudioAuthoringServices(this IServiceCollection services)
    {
        services.AddSingleton<AppAuthoringChatSessionFactory>();
        services.AddSingleton<WorkflowGeneratePromptCatalog>();
        services.AddSingleton<WorkflowGenerateOrchestrator>();
        services.AddSingleton<WorkflowGenerateActorService>();
        services.AddSingleton<ScriptGeneratePromptCatalog>();
        services.AddSingleton<ScriptGenerateOrchestrator>();
        services.AddSingleton<ScriptGenerateActorService>();
        services.AddSingleton<ScriptEditorValidationService>();
        return services;
    }

    private static IServiceCollection AddStudioProjectionReadModelProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(x => x.ServiceType == typeof(IProjectionDocumentReader<GAgentRegistryCurrentStateDocument, string>)))
            return services;

        var elasticsearchEnabled = ResolveElasticsearchDocumentEnabled(configuration);
        var inMemoryEnabled = ResolveOptionalBool(
            configuration["Projection:Document:Providers:InMemory:Enabled"],
            fallbackValue: !elasticsearchEnabled);
        var providerCount = (elasticsearchEnabled ? 1 : 0) + (inMemoryEnabled ? 1 : 0);
        if (providerCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one document projection provider must be enabled for Studio.");
        }

        if (elasticsearchEnabled)
        {
            AddElasticsearchDocumentProjectionStore<GAgentRegistryCurrentStateDocument>(services, configuration);
            AddElasticsearchDocumentProjectionStore<ConnectorCatalogCurrentStateDocument>(services, configuration);
            AddElasticsearchDocumentProjectionStore<RoleCatalogCurrentStateDocument>(services, configuration);
            AddElasticsearchDocumentProjectionStore<UserMemoryCurrentStateDocument>(services, configuration);
            AddElasticsearchDocumentProjectionStore<StreamingProxyParticipantCurrentStateDocument>(services, configuration);
            AddElasticsearchDocumentProjectionStore<ChatHistoryIndexCurrentStateDocument>(services, configuration);
            AddElasticsearchDocumentProjectionStore<ChatConversationCurrentStateDocument>(services, configuration);
            AddElasticsearchDocumentProjectionStore<UserConfigCurrentStateDocument>(services, configuration);
        }
        else
        {
            AddInMemoryDocumentProjectionStore<GAgentRegistryCurrentStateDocument>(services);
            AddInMemoryDocumentProjectionStore<ConnectorCatalogCurrentStateDocument>(services);
            AddInMemoryDocumentProjectionStore<RoleCatalogCurrentStateDocument>(services);
            AddInMemoryDocumentProjectionStore<UserMemoryCurrentStateDocument>(services);
            AddInMemoryDocumentProjectionStore<StreamingProxyParticipantCurrentStateDocument>(services);
            AddInMemoryDocumentProjectionStore<ChatHistoryIndexCurrentStateDocument>(services);
            AddInMemoryDocumentProjectionStore<ChatConversationCurrentStateDocument>(services);
            AddInMemoryDocumentProjectionStore<UserConfigCurrentStateDocument>(services);
        }

        return services;
    }

    private static void AddElasticsearchDocumentProjectionStore<TReadModel>(
        IServiceCollection services,
        IConfiguration configuration)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        services.AddElasticsearchDocumentProjectionStore<TReadModel, string>(
            optionsFactory: _ => BuildElasticsearchDocumentOptions(configuration),
            metadataFactory: sp => sp.GetRequiredService<IProjectionDocumentMetadataProvider<TReadModel>>().Metadata,
            keySelector: readModel => readModel.ActorId,
            keyFormatter: key => key,
            typeRegistry: BuildStudioStateTypeRegistry());
    }

    private static void AddInMemoryDocumentProjectionStore<TReadModel>(
        IServiceCollection services)
        where TReadModel : class, IProjectionReadModel<TReadModel>, new()
    {
        services.AddInMemoryDocumentProjectionStore<TReadModel, string>(
            keySelector: readModel => readModel.ActorId,
            keyFormatter: key => key,
            defaultSortSelector: readModel => readModel.UpdatedAt);
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

    private static bool ResolveOptionalBool(string? rawValue, bool fallbackValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallbackValue;
        if (!bool.TryParse(rawValue, out var parsed))
            throw new InvalidOperationException($"Invalid boolean value '{rawValue}'.");

        return parsed;
    }

    private static TypeRegistry BuildStudioStateTypeRegistry()
    {
        return TypeRegistry.FromMessages(
            UserConfigGAgentState.Descriptor,
            GAgentRegistryState.Descriptor,
            ConnectorCatalogState.Descriptor,
            RoleCatalogState.Descriptor,
            UserMemoryState.Descriptor,
            StreamingProxyParticipantGAgentState.Descriptor,
            ChatHistoryIndexState.Descriptor,
            ChatConversationState.Descriptor);
    }
}
