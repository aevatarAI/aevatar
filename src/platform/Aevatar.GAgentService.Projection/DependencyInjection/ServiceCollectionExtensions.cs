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

        services.AddEventSinkProjectionRuntimeCore<
            ServiceCatalogProjectionContext,
            IReadOnlyList<string>,
            ServiceCatalogRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            ServiceDeploymentCatalogProjectionContext,
            IReadOnlyList<string>,
            ServiceDeploymentCatalogRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            ServiceServingSetProjectionContext,
            IReadOnlyList<string>,
            ServiceServingSetRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            ServiceRolloutProjectionContext,
            IReadOnlyList<string>,
            ServiceRolloutRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            ServiceTrafficViewProjectionContext,
            IReadOnlyList<string>,
            ServiceTrafficViewRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            ServiceRevisionCatalogProjectionContext,
            IReadOnlyList<string>,
            ServiceRevisionCatalogRuntimeLease,
            EventEnvelope>();

        services.TryAddSingleton<IProjectionPortActivationService<ServiceCatalogRuntimeLease>, ServiceCatalogProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceCatalogRuntimeLease>, ServiceCatalogProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortActivationService<ServiceDeploymentCatalogRuntimeLease>, ServiceDeploymentCatalogProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceDeploymentCatalogRuntimeLease>, ServiceDeploymentCatalogProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortActivationService<ServiceServingSetRuntimeLease>, ServiceServingSetProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceServingSetRuntimeLease>, ServiceServingSetProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortActivationService<ServiceRolloutRuntimeLease>, ServiceRolloutProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceRolloutRuntimeLease>, ServiceRolloutProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortActivationService<ServiceTrafficViewRuntimeLease>, ServiceTrafficViewProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceTrafficViewRuntimeLease>, ServiceTrafficViewProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortActivationService<ServiceRevisionCatalogRuntimeLease>, ServiceRevisionCatalogProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceRevisionCatalogRuntimeLease>, ServiceRevisionCatalogProjectionReleaseService>();
        services.TryAddSingleton<ServiceCatalogProjectionPortService>();
        services.TryAddSingleton<ServiceDeploymentCatalogProjectionPortService>();
        services.TryAddSingleton<ServiceServingSetProjectionPortService>();
        services.TryAddSingleton<ServiceRolloutProjectionPortService>();
        services.TryAddSingleton<ServiceTrafficViewProjectionPortService>();
        services.TryAddSingleton<ServiceRevisionCatalogProjectionPortService>();
        services.TryAddSingleton<IServiceCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceCatalogProjectionPortService>());
        services.TryAddSingleton<IServiceDeploymentCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceDeploymentCatalogProjectionPortService>());
        services.TryAddSingleton<IServiceServingSetProjectionPort>(sp => sp.GetRequiredService<ServiceServingSetProjectionPortService>());
        services.TryAddSingleton<IServiceRolloutProjectionPort>(sp => sp.GetRequiredService<ServiceRolloutProjectionPortService>());
        services.TryAddSingleton<IServiceTrafficViewProjectionPort>(sp => sp.GetRequiredService<ServiceTrafficViewProjectionPortService>());
        services.TryAddSingleton<IServiceRevisionCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceRevisionCatalogProjectionPortService>());
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
}
