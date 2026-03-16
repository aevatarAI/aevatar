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
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceDeploymentCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceRevisionCatalogProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceServingSetProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceRolloutProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
            },
            static context => context.RootActorId);
        services.AddServiceProjectionRuntime(
            static (rootActorId, projectionName) => new ServiceTrafficViewProjectionContext
            {
                RootActorId = rootActorId,
                ProjectionKind = projectionName,
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
            IProjectionMaterializer<ServiceCatalogProjectionContext>,
            ServiceCatalogProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ServiceDeploymentCatalogProjectionContext>,
            ServiceDeploymentCatalogProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ServiceServingSetProjectionContext>,
            ServiceServingSetProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ServiceRolloutProjectionContext>,
            ServiceRolloutProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ServiceTrafficViewProjectionContext>,
            ServiceTrafficViewProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionMaterializer<ServiceRevisionCatalogProjectionContext>,
            ServiceRevisionCatalogProjector>());

        return services;
    }

    private static IServiceCollection AddServiceProjectionRuntime<TContext>(
        this IServiceCollection services,
        Func<string, string, TContext> contextFactory,
        Func<TContext, string> rootActorIdSelector)
        where TContext : class, IProjectionMaterializationContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(rootActorIdSelector);

        services.AddProjectionMaterializationRuntimeCore<
            TContext,
            ServiceProjectionRuntimeLease<TContext>>();
        services.TryAddSingleton<IProjectionMaterializationActivationService<ServiceProjectionRuntimeLease<TContext>>>(sp =>
        {
            return new ContextProjectionMaterializationActivationService<ServiceProjectionRuntimeLease<TContext>, TContext>(
                sp.GetRequiredService<IProjectionMaterializationLifecycleService<TContext, ServiceProjectionRuntimeLease<TContext>>>(),
                (request, _) => contextFactory(request.RootActorId, request.ProjectionKind),
                context => new ServiceProjectionRuntimeLease<TContext>(rootActorIdSelector(context), context));
        });
        services.TryAddSingleton<IProjectionMaterializationReleaseService<ServiceProjectionRuntimeLease<TContext>>, ContextProjectionMaterializationReleaseService<ServiceProjectionRuntimeLease<TContext>, TContext>>();

        return services;
    }
}
