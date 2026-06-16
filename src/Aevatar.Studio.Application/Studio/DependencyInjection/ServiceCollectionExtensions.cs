using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Authoring;
using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Application.Studio.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioApplication(this IServiceCollection services)
    {
        services.AddSingleton<WorkflowGraphMapper>();
        services.AddSingleton<TextDiffService>();
        services.AddSingleton<WorkflowEditorService>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<ExecutionService>();
        services.AddSingleton<ConnectorService>();
        services.AddSingleton<RoleCatalogService>();
        services.AddSingleton<SettingsService>();
        // Refactor (iter21/cluster-001):
        //   Old pattern: Host registered Ask AI authoring orchestrators and fake actor services.
        //   New principle: Application owns request-scoped authoring preview orchestration behind typed ports.
        services.AddSingleton<WorkflowAuthoringPromptCatalog>();
        services.AddSingleton<ScriptAuthoringPromptCatalog>();
        services.AddSingleton<WorkflowAuthoringPreviewGenerator>();
        services.AddSingleton<ScriptAuthoringPreviewGenerator>();
        services.TryAddSingleton<IStudioAuthoringPreviewApplicationService, StudioAuthoringPreviewApplicationService>();
        services.TryAddSingleton<IStudioMemberService, StudioMemberService>();
        services.TryAddSingleton<IStudioTeamService, StudioTeamService>();
        services.TryAddSingleton<IStudioTeamGAgentStreamInvocationService, StudioTeamGAgentStreamInvocationService>();
        services.AddOptions<UserLlmSettingsOptions>();
        services.Replace(ServiceDescriptor.Singleton<ITeamEntryMemberResolver, StudioTeamEntryMemberResolver>());
        services.TryAddSingleton<UserLlmPreferenceWriter>();
        services.TryAddSingleton<IChannelUserLlmPreferencePort, ChannelUserLlmPreferencePort>();
        services.TryAddSingleton<IUserConfigService, UserConfigService>();
        services.TryAddSingleton<IUserLlmPreferenceService, UserLlmPreferenceService>();

        // Override the platform resolver so existing member-first invoke /
        // runs / binding routes resolve to the same
        // publishedServiceId Studio's bind path persisted on the member
        // authority.
        services.Replace(ServiceDescriptor.Singleton<IMemberPublishedServiceResolver, StudioAwareMemberPublishedServiceResolver>());
        return services;
    }
}
