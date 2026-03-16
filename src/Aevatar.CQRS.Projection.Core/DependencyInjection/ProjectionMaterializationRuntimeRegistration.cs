using Aevatar.CQRS.Projection.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared registration helpers for durable committed-observation materialization runtime components.
/// </summary>
public static class ProjectionMaterializationRuntimeRegistration
{
    public static IServiceCollection AddProjectionMaterializationRuntimeCore<TContext, TRuntimeLease>(
        this IServiceCollection services)
        where TContext : class, IProjectionMaterializationContext
        where TRuntimeLease : ProjectionRuntimeLeaseBase, IProjectionContextRuntimeLease<TContext>, IProjectionStreamSubscriptionRuntimeLease
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProjectionMaterializationCoordinator<TContext>, ProjectionMaterializationCoordinator<TContext>>();
        services.TryAddSingleton<IProjectionMaterializationDispatcher<TContext>, ProjectionMaterializationDispatcher<TContext>>();
        services.TryAddSingleton<IProjectionMaterializationSubscriptionRegistry<TContext, TRuntimeLease>, ProjectionMaterializationSubscriptionRegistry<TContext, TRuntimeLease>>();
        services.TryAddSingleton<IProjectionMaterializationLifecycleService<TContext, TRuntimeLease>, ProjectionMaterializationLifecycleService<TContext, TRuntimeLease>>();

        return services;
    }
}
