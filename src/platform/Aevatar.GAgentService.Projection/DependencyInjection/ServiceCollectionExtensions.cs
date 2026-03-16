using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Metadata;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGAgentServiceProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));

        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootActorId}",
                RootActorId = rootActorId,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceDeploymentCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootActorId}",
                RootActorId = rootActorId,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceRevisionCatalogProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootActorId}",
                RootActorId = rootActorId,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceServingSetProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootActorId}",
                RootActorId = rootActorId,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceRolloutProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootActorId}",
                RootActorId = rootActorId,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceTrafficViewProjectionContext
            {
                ProjectionId = $"{projectionName}:{rootActorId}",
                RootActorId = rootActorId,
            },
            static context => context.RootActorId);

        services.TryAddSingleton<IServiceCatalogProjectionPort, ServiceCatalogProjectionPort>();
        services.TryAddSingleton<IServiceDeploymentCatalogProjectionPort, ServiceDeploymentCatalogProjectionPort>();
        services.TryAddSingleton<IServiceServingSetProjectionPort, ServiceServingSetProjectionPort>();
        services.TryAddSingleton<IServiceRolloutProjectionPort, ServiceRolloutProjectionPort>();
        services.TryAddSingleton<IServiceTrafficViewProjectionPort, ServiceTrafficViewProjectionPort>();
        services.TryAddSingleton<IServiceRevisionCatalogProjectionPort, ServiceRevisionCatalogProjectionPort>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>, ServiceCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceDeploymentCatalogReadModel>, ServiceDeploymentCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceServingSetReadModel>, ServiceServingSetReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRolloutReadModel>, ServiceRolloutReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceTrafficViewReadModel>, ServiceTrafficViewReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>, ServiceRevisionCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceCatalogQueryReader, ServiceCatalogQueryReader>();
        services.TryAddSingleton<IServiceDeploymentCatalogQueryReader, ServiceDeploymentCatalogQueryReader>();
        services.TryAddSingleton<IServiceServingSetQueryReader, ServiceServingSetQueryReader>();
        services.TryAddSingleton<IServiceRolloutQueryReader, ServiceRolloutQueryReader>();
        services.TryAddSingleton<IServiceTrafficViewQueryReader, ServiceTrafficViewQueryReader>();
        services.TryAddSingleton<IServiceRevisionCatalogQueryReader, ServiceRevisionCatalogQueryReader>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceCatalogProjectionContext, IReadOnlyList<string>>,
            ServiceCatalogProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceDeploymentCatalogProjectionContext, IReadOnlyList<string>>,
            ServiceDeploymentCatalogProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceServingSetProjectionContext, IReadOnlyList<string>>,
            ServiceServingSetProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceRolloutProjectionContext, IReadOnlyList<string>>,
            ServiceRolloutProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceTrafficViewProjectionContext, IReadOnlyList<string>>,
            ServiceTrafficViewProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceRevisionCatalogProjectionContext, IReadOnlyList<string>>,
            ServiceRevisionCatalogProjector>());

        return services;
    }

    private static IServiceCollection AddServiceProjectionRuntime<TContext>(
        this IServiceCollection services,
        Func<string, string, TContext> contextFactory,
        Func<TContext, string> rootActorIdSelector)
        where TContext : class, IProjectionContext, IProjectionStreamSubscriptionContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(rootActorIdSelector);

        services.AddEventSinkProjectionRuntimeCore<
            TContext,
            IReadOnlyList<string>,
            ServiceProjectionRuntimeLease<TContext>,
            EventEnvelope>();
        services.TryAddSingleton<IProjectionPortActivationService<ServiceProjectionRuntimeLease<TContext>>>(sp =>
        {
            return new ContextProjectionActivationService<ServiceProjectionRuntimeLease<TContext>, TContext, IReadOnlyList<string>>(
                sp.GetRequiredService<IProjectionLifecycleService<TContext, IReadOnlyList<string>>>(),
                (rootActorId, projectionName, input, commandId, ct) =>
                {
                    _ = input;
                    _ = commandId;
                    _ = ct;
                    return contextFactory(rootActorId, projectionName);
                },
                context => new ServiceProjectionRuntimeLease<TContext>(rootActorIdSelector(context), context));
        });
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceProjectionRuntimeLease<TContext>>, ContextProjectionReleaseService<ServiceProjectionRuntimeLease<TContext>, TContext, IReadOnlyList<string>>>();

        return services;
    }
}
