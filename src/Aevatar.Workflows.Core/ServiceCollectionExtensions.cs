using Aevatar.AI.Abstractions.Agents;
using Aevatar.Workflows.Core.Connectors;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.EventModules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflows.Core;

/// <summary>
/// DI helpers for Cognitive workflow features.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Cognitive defaults:
    /// - <see cref="WorkflowModuleFactory"/>
    /// - <see cref="IConnectorRegistry"/> (in-memory)
    /// </summary>
    public static IServiceCollection AddAevatarWorkflows(this IServiceCollection services)
    {
        services.TryAddSingleton<IEventModuleFactory, WorkflowModuleFactory>();
        services.TryAddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
        services.TryAddSingleton<IRoleAgentTypeResolver, ReflectionRoleAgentTypeResolver>();
        return services;
    }
}
