using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.AevatarInvocation;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAevatarInvocationTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AevatarInvocationDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, InvokeGAgentToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, InvokeTeamToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, InvokeMemberToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, StartWorkflowToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, ObserveRunToolSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, ReadWorkflowRunArtifactToolSource>());
        return services;
    }
}
