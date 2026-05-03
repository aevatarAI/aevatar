using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
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
        services.TryAddSingleton<IStudioMemberService, StudioMemberService>();
        services.TryAddSingleton<IStudioTeamService, StudioTeamService>();
        services.TryAddSingleton<IUserLlmPreferenceService, UserLlmPreferenceService>();

        // Override the platform's deterministic resolver so existing
        // member-first invoke / runs / binding routes resolve to the same
        // publishedServiceId Studio's bind path persisted on the member
        // authority. Platform registers the default with TryAddSingleton, so
        // a plain Replace here wins for IServiceProvider.GetService<T>.
        services.Replace(ServiceDescriptor.Singleton<IMemberPublishedServiceResolver, StudioAwareMemberPublishedServiceResolver>());
        return services;
    }
}
