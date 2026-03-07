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
    /// Registers workflow core primitive packs and primitive executors.
    /// </summary>
    public static IServiceCollection AddAevatarWorkflow(this IServiceCollection services)
    {
        services.AddWorkflowPrimitivePack<WorkflowCorePrimitivePack>();
        return services;
    }

    public static IServiceCollection AddWorkflowPrimitivePack<TPrimitivePack>(this IServiceCollection services)
        where TPrimitivePack : class, IWorkflowPrimitivePack
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowPrimitivePack, TPrimitivePack>());
        return services;
    }
}
