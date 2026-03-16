using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared registration helpers for actorized durable materialization components.
/// </summary>
public static class ProjectionMaterializationRuntimeRegistration
{
    public static IServiceCollection AddProjectionMaterializationRuntimeCore<TContext, TRuntimeLease>(
        this IServiceCollection services)
        where TContext : class, IProjectionMaterializationContext
        where TRuntimeLease : IProjectionRuntimeLease
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
