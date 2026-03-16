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
            new ServiceProjectionDescriptor<ServiceCatalogProjectionContext>(
                static (rootActorId, projectionName) => new ServiceCatalogProjectionContext
                {
                    ProjectionId = $"{projectionName}:{rootActorId}",
                    RootActorId = rootActorId,
                },
                static context => context.RootActorId));
        services.AddServiceProjectionRuntime(
            new ServiceProjectionDescriptor<ServiceDeploymentCatalogProjectionContext>(
                static (rootActorId, projectionName) => new ServiceDeploymentCatalogProjectionContext
                {
                    ProjectionId = $"{projectionName}:{rootActorId}",
                    RootActorId = rootActorId,
                },
                static context => context.RootActorId));
        services.AddServiceProjectionRuntime(
            new ServiceProjectionDescriptor<ServiceRevisionCatalogProjectionContext>(
                static (rootActorId, projectionName) => new ServiceRevisionCatalogProjectionContext
                {
                    ProjectionId = $"{projectionName}:{rootActorId}",
                    RootActorId = rootActorId,
                },
                static context => context.RootActorId));
        services.AddServiceProjectionRuntime(
            new ServiceProjectionDescriptor<ServiceServingSetProjectionContext>(
                static (rootActorId, projectionName) => new ServiceServingSetProjectionContext
                {
                    ProjectionId = $"{projectionName}:{rootActorId}",
                    RootActorId = rootActorId,
                },
                static context => context.RootActorId));
        services.AddServiceProjectionRuntime(
            new ServiceProjectionDescriptor<ServiceRolloutProjectionContext>(
                static (rootActorId, projectionName) => new ServiceRolloutProjectionContext
                {
                    ProjectionId = $"{projectionName}:{rootActorId}",
                    RootActorId = rootActorId,
                },
                static context => context.RootActorId));
        services.AddServiceProjectionRuntime(
            new ServiceProjectionDescriptor<ServiceTrafficViewProjectionContext>(
                static (rootActorId, projectionName) => new ServiceTrafficViewProjectionContext
                {
                    ProjectionId = $"{projectionName}:{rootActorId}",
                    RootActorId = rootActorId,
                },
                static context => context.RootActorId));

        services.TryAddSingleton<ServiceProjectionPortServices>();
        services.TryAddSingleton<IServiceCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceProjectionPortServices>());
        services.TryAddSingleton<IServiceDeploymentCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceProjectionPortServices>());
        services.TryAddSingleton<IServiceServingSetProjectionPort>(sp => sp.GetRequiredService<ServiceProjectionPortServices>());
        services.TryAddSingleton<IServiceRolloutProjectionPort>(sp => sp.GetRequiredService<ServiceProjectionPortServices>());
        services.TryAddSingleton<IServiceTrafficViewProjectionPort>(sp => sp.GetRequiredService<ServiceProjectionPortServices>());
        services.TryAddSingleton<IServiceRevisionCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceProjectionPortServices>());
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
        ServiceProjectionDescriptor<TContext> descriptor)
        where TContext : class, IProjectionContext, IProjectionStreamSubscriptionContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddEventSinkProjectionRuntimeCore<
            TContext,
            IReadOnlyList<string>,
            ServiceProjectionRuntimeLease<TContext>,
            EventEnvelope>();
        services.TryAddSingleton(descriptor);
        services.TryAddSingleton<IProjectionPortActivationService<ServiceProjectionRuntimeLease<TContext>>, ServiceProjectionActivationService<TContext>>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceProjectionRuntimeLease<TContext>>, ServiceProjectionReleaseService<TContext>>();

        return services;
    }
}
