using Aevatar.Workflow.Core.Connectors;
using Aevatar.Foundation.Abstractions.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Core;

/// <summary>
/// DI helpers for Cognitive workflow features.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Cognitive defaults:
    /// - <see cref="IConnectorRegistry"/> (in-memory)
    /// </summary>
    public static IServiceCollection AddAevatarWorkflow(this IServiceCollection services)
    {
        services.AddWorkflowModulePack<WorkflowCoreModulePack>();
        services.TryAddSingleton<IConnectorRegistry, InMemoryConnectorRegistry>();
        return services;
    }

    public static IServiceCollection AddWorkflowModulePack<TModulePack>(this IServiceCollection services)
        where TModulePack : class, IWorkflowModulePack
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowModulePack, TModulePack>());
        return services;
    }
}
