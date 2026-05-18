using Aevatar.CQRS.Projection.Core.Observability;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf;
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

        services.TryAddSingleton<TMaterializer>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProjectionMaterializer<TContext>, ObservedProjectionMaterializer<TContext, TMaterializer>>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ICurrentStateProjectionMaterializer<TContext>, ObservedCurrentStateProjectionMaterializer<TContext, TMaterializer>>());
        return services;
    }

    public static IServiceCollection AddCurrentStateProjection<TContext, TState, TReadModel>(
        this IServiceCollection services,
        Func<TContext, TState, CurrentStateProjectionInfo, TReadModel> map)
        where TContext : class, IProjectionMaterializationContext
        where TState : class, IMessage<TState>, new()
        where TReadModel : class, IProjectionReadModel
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(map);

        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton(map);
        services.TryAddSingleton<CurrentStateProjectionMaterializer<TContext, TState, TReadModel>>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProjectionMaterializer<TContext>, ObservedProjectionMaterializer<TContext, CurrentStateProjectionMaterializer<TContext, TState, TReadModel>>>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ICurrentStateProjectionMaterializer<TContext>, ObservedCurrentStateProjectionMaterializer<TContext, CurrentStateProjectionMaterializer<TContext, TState, TReadModel>>>());
        return services;
    }

    public static IServiceCollection AddProjectionArtifactMaterializer<TContext, TMaterializer>(
        this IServiceCollection services)
        where TContext : class, IProjectionMaterializationContext
        where TMaterializer : class, IProjectionArtifactMaterializer<TContext>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TMaterializer>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProjectionMaterializer<TContext>, ObservedProjectionMaterializer<TContext, TMaterializer>>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProjectionArtifactMaterializer<TContext>, ObservedProjectionArtifactMaterializer<TContext, TMaterializer>>());
        return services;
    }
}
