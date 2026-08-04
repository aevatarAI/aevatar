using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.CQRS.Core.DependencyInjection;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Authoring;
using Aevatar.Studio.Application.Studio.ProjectionRecovery;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgents.ChatHistory.DependencyInjection;
using Aevatar.Studio.Infrastructure.Authoring;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Infrastructure.Middleware;
using Aevatar.Studio.Infrastructure.Serialization;
using Aevatar.Studio.Infrastructure.Storage;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddChatHistoryGAgents();
        services.Replace(ServiceDescriptor.Singleton<
            IChatConversationContinuationAdmissionReader,
            ProjectionChatConversationContinuationAdmissionReader>());
        services.AddCqrsCore();
        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(
            typeof(Aevatar.GAgents.ConnectorCatalog.ConnectorCatalogGAgent).Assembly,
            typeof(Aevatar.GAgents.Registry.GAgentRegistryGAgent).Assembly,
            typeof(Aevatar.GAgents.RoleCatalog.RoleCatalogGAgent).Assembly,
            typeof(Aevatar.GAgents.UserMemory.UserMemoryGAgent).Assembly));
        services.AddStudioActorCommandDispatch();
        services.Configure<StudioStorageOptions>(configuration.GetSection("Studio:Storage"));
        services.Configure<ConnectorCatalogStorageOptions>(configuration.GetSection(ConnectorCatalogStorageOptions.SectionName));
        services.AddSingleton(WorkflowCompatibilityProfile.AevatarV1);
        services.AddSingleton<WorkflowDocumentNormalizer>();
        services.AddSingleton<WorkflowValidator>();
        services.AddSingleton<IWorkflowYamlDocumentService, YamlWorkflowDocumentService>();
        // Refactor (iter16/cluster-meta-studio-actor-substrate):
        //   Old: FileStudioWorkspaceStore was a shadow store reading/writing JSON files in workspace dir, with no clear actor ownership of workspace facts
        //   New principle: workspace facts authoritatively owned by StudioWorkspaceGAgent (per CLAUDE.md "权威状态" + Auric 2026-05-19 "架构级清晰")
        services.AddSingleton<IStudioWorkspaceCommandPort, ActorDispatchStudioWorkspaceCommandPort>();
        services.TryAddSingleton<
            IStudioWorkspaceProjectionRepublishPort,
            ActorDispatchStudioWorkspaceProjectionRepublishPort>();
        services.AddSingleton<IUserConfigDefaults, ConfiguredUserConfigDefaults>();
        services.AddSingleton<IConnectorCatalogImportParser, ConnectorCatalogImportParser>();
        services.AddSingleton<IRoleCatalogImportParser, RoleCatalogImportParser>();
        services.AddSingleton<StudioLocalCatalogImportReader>();
        services.AddSingleton<IStudioLocalConnectorCatalogImportReader>(sp => sp.GetRequiredService<StudioLocalCatalogImportReader>());
        services.AddSingleton<IStudioLocalRoleCatalogImportReader>(sp => sp.GetRequiredService<StudioLocalCatalogImportReader>());
        // chrono-storage blob client retained for media file uploads (ExplorerEndpoints)
        services.AddSingleton<ChronoStorageCatalogBlobClient>();
        // ── Actor-backed stores (replacing ChronoStorage* implementations) ──
        services.AddSingleton<ActorBackedGAgentRegistryPorts>();
        services.AddSingleton<IGAgentActorRegistryCommandPort>(sp => sp.GetRequiredService<ActorBackedGAgentRegistryPorts>());
        services.AddSingleton<IGAgentActorRegistryQueryPort>(sp => sp.GetRequiredService<ActorBackedGAgentRegistryPorts>());
        services.AddSingleton<IScopeResourceAdmissionPort>(sp => sp.GetRequiredService<ActorBackedGAgentRegistryPorts>());
        services.AddSingleton<INyxIdUserLlmPreferencesStore, ActorBackedNyxIdUserLlmPreferencesStore>();
        services.AddSingleton<ActorBackedConnectorCatalogStore>();
        services.AddSingleton<IConnectorCatalogQueryPort>(sp => sp.GetRequiredService<ActorBackedConnectorCatalogStore>());
        services.AddSingleton<IConnectorCatalogCommandPort>(sp => sp.GetRequiredService<ActorBackedConnectorCatalogStore>());
        services.AddSingleton<ActorBackedRoleCatalogStore>();
        services.AddSingleton<IRoleCatalogQueryPort>(sp => sp.GetRequiredService<ActorBackedRoleCatalogStore>());
        services.AddSingleton<IRoleCatalogCommandPort>(sp => sp.GetRequiredService<ActorBackedRoleCatalogStore>());
        services.AddSingleton<ActorBackedChatHistoryStore>();
        services.AddSingleton<IChatHistoryQueryPort>(sp => sp.GetRequiredService<ActorBackedChatHistoryStore>());
        services.AddSingleton<IChatHistoryCommandPort>(sp => sp.GetRequiredService<ActorBackedChatHistoryStore>());
        services.AddSingleton<IWorkflowChatHistoryCreateRecoveryReadPort>(sp => sp.GetRequiredService<ActorBackedChatHistoryStore>());
        services.AddSingleton<
            INyxIdChatConversationStateQueryPort,
            ProjectionNyxIdChatConversationStateQueryPort>();
        // Script runtime activity reads the scripting capability's native-document read model;
        // hosts composed without scripting have no reader, and consumers of this port already
        // treat it as optional (resolved via GetService).
        if (services.Any(x => x.ServiceType == typeof(
                Aevatar.CQRS.Projection.Stores.Abstractions.IProjectionDocumentReader<
                    Aevatar.Scripting.Projection.ReadModels.ScriptNativeDocumentReadModel, string>)))
        {
            services.AddSingleton<IScriptRuntimeActivityQueryPort, ScriptNativeDocumentRuntimeActivityQueryPort>();
        }
        services.AddSingleton<ILLMCallMiddleware, UserMemoryInjectionMiddleware>();
        services.AddSingleton<ILLMCallMiddleware, ConnectedServicesContextMiddleware>();
        // Refactor (iter21/cluster-001):
        //   Old pattern: Host constructed ChatRuntime for Studio Ask AI preview sessions.
        //   New principle: Infrastructure implements the typed Application LLM stream port with ChatStreamAsync.
        services.AddSingleton<IStudioAuthoringLLMStreamPort, ChatRuntimeStudioAuthoringLLMStreamPort>();
        return services;
    }
}
