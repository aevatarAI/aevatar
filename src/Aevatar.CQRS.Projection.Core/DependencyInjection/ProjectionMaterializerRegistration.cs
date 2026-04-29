using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

/// <summary>
/// Shared helpers for registering semantically classified durable materializers.
/// </summary>
public static class ProjectionMaterializerRegistration
{
    public static IServiceCollection AddCurrentStateProjectionMaterializer<TContext, TMaterializer>(
        this IServiceCollection services)
        where TContext : class, IProjectionMaterializationContext
        where TMaterializer : class, ICurrentStateProjectionMaterializer<TContext>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionMaterializer<TContext>, TMaterializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICurrentStateProjectionMaterializer<TContext>, TMaterializer>());
        return services;
    }

    public static IServiceCollection AddProjectionArtifactMaterializer<TContext, TMaterializer>(
        this IServiceCollection services)
        where TContext : class, IProjectionMaterializationContext
        where TMaterializer : class, IProjectionArtifactMaterializer<TContext>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionMaterializer<TContext>, TMaterializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProjectionArtifactMaterializer<TContext>, TMaterializer>());
        return services;
    }

}
