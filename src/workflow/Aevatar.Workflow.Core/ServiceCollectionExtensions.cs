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
    /// Registers workflow core module packs and primitive handlers.
    /// </summary>
    public static IServiceCollection AddAevatarWorkflow(this IServiceCollection services)
    {
        services.AddWorkflowModulePack<WorkflowCoreModulePack>();
        return services;
    }

    public static IServiceCollection AddWorkflowModulePack<TModulePack>(this IServiceCollection services)
        where TModulePack : class, IWorkflowModulePack
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowModulePack, TModulePack>());
        return services;
    }
}
