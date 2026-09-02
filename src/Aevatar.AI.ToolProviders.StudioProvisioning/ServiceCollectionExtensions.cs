using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers local, typed Studio provisioning tools as <see cref="IAgentToolSource"/>
    /// implementations. Each tool resolves a narrow port from Studio's abstractions
    /// package, so the tool provider never calls the local host through HTTP or NyxID
    /// proxy and never references the Studio application implementation assembly.
    /// </summary>
    public static IServiceCollection AddStudioProvisioningTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ProvisionWorkflowScheduleToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, CreateStudioTeamToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, StudioTeamQueryToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, CreateStudioMemberToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, CreateStudioMemberWorkflowDraftToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, StudioMemberQueryToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, StudioWorkflowQueryToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, StudioScheduleQueryToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, BindStudioMemberWorkflowToolSource>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ScheduleStudioMemberWorkflowToolSource>());
        return services;
    }
}
