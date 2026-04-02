using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.Middleware;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Domain.Studio.Compatibility;
using Aevatar.Studio.Domain.Studio.Services;
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
        services.AddSingleton<ChronoStorageCatalogBlobClient>();
        services.AddSingleton<IConnectorCatalogStore, ChronoStorageConnectorCatalogStore>();
        services.AddSingleton<IRoleCatalogStore, ChronoStorageRoleCatalogStore>();
        services.AddSingleton<IUserConfigStore, ChronoStorageUserConfigStore>();
        services.AddSingleton<INyxIdUserLlmPreferencesStore, ChronoStorageNyxIdUserLlmPreferencesStore>();
        services.AddSingleton<IUserMemoryStore, ChronoStorageUserMemoryStore>();
        services.AddSingleton<ILLMCallMiddleware, UserMemoryInjectionMiddleware>();
        services.AddSingleton<IGAgentActorStore, ChronoStorageGAgentActorStore>();
        services.AddSingleton<IChatHistoryStore, ChronoStorageChatHistoryStore>();
        services.AddSingleton<IWorkflowStoragePort, ChronoStorageWorkflowStoragePort>();
        services.AddSingleton<IScriptStoragePort, ChronoStorageScriptStoragePort>();
        services.AddSingleton<IAevatarSettingsStore, FileAevatarSettingsStore>();
        return services;
    }
}
