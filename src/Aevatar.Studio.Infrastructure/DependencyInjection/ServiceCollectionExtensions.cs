using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Services;
using Aevatar.Studio.Infrastructure.ActorBacked;
using Aevatar.Studio.Infrastructure.Middleware;
using Aevatar.Studio.Infrastructure.Serialization;
using Aevatar.Studio.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<StudioStorageOptions>(configuration.GetSection("Studio:Storage"));
        services.Configure<ConnectorCatalogStorageOptions>(configuration.GetSection(ConnectorCatalogStorageOptions.SectionName));
        services.AddSingleton(WorkflowCompatibilityProfile.AevatarV1);
        services.AddSingleton<WorkflowDocumentNormalizer>();
        services.AddSingleton<WorkflowValidator>();
        services.AddSingleton<IWorkflowYamlDocumentService, YamlWorkflowDocumentService>();
        services.AddSingleton<FileStudioWorkspaceStore>();
        services.AddSingleton<IStudioWorkspaceStore>(sp => sp.GetRequiredService<FileStudioWorkspaceStore>());
        services.AddSingleton<IConnectorCatalogImportParser, ConnectorCatalogImportParser>();
        services.AddSingleton<IRoleCatalogImportParser, RoleCatalogImportParser>();
        // chrono-storage blob client retained for media file uploads (ExplorerEndpoints)
        services.AddSingleton<ChronoStorageCatalogBlobClient>();
        // ── Actor-backed stores (replacing ChronoStorage* implementations) ──
        services.AddSingleton<IGAgentActorStore, ActorBackedGAgentActorStore>();
        services.AddSingleton<IStreamingProxyParticipantStore, ActorBackedStreamingProxyParticipantStore>();
        services.AddSingleton<INyxIdUserLlmPreferencesStore, ActorBackedNyxIdUserLlmPreferencesStore>();
        services.AddSingleton<IUserMemoryStore, ActorBackedUserMemoryStore>();
        services.AddSingleton<IConnectorCatalogStore, ActorBackedConnectorCatalogStore>();
        services.AddSingleton<IRoleCatalogStore, ActorBackedRoleCatalogStore>();
        services.AddSingleton<IChatHistoryStore, ActorBackedChatHistoryStore>();
        services.AddSingleton<ILLMCallMiddleware, UserMemoryInjectionMiddleware>();
        services.AddSingleton<ILLMCallMiddleware, ConnectedServicesContextMiddleware>();
        services.AddSingleton<IWorkflowStoragePort, ChronoStorageWorkflowStoragePort>();
        services.AddSingleton<IScriptStoragePort, ChronoStorageScriptStoragePort>();
        services.AddSingleton<IAevatarSettingsStore, FileAevatarSettingsStore>();
        return services;
    }
}
