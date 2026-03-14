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
            ServiceRevisionCatalogProjectionContext,
            IReadOnlyList<string>,
            ServiceRevisionCatalogRuntimeLease,
            EventEnvelope>();

        services.TryAddSingleton<IProjectionPortActivationService<ServiceCatalogRuntimeLease>, ServiceCatalogProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceCatalogRuntimeLease>, ServiceCatalogProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortActivationService<ServiceRevisionCatalogRuntimeLease>, ServiceRevisionCatalogProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceRevisionCatalogRuntimeLease>, ServiceRevisionCatalogProjectionReleaseService>();
        services.TryAddSingleton<ServiceCatalogProjectionPortService>();
        services.TryAddSingleton<ServiceRevisionCatalogProjectionPortService>();
        services.TryAddSingleton<IServiceCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceCatalogProjectionPortService>());
        services.TryAddSingleton<IServiceRevisionCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceRevisionCatalogProjectionPortService>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceCatalogReadModel>, ServiceCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceRevisionCatalogReadModel>, ServiceRevisionCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceCatalogQueryReader, ServiceCatalogQueryReader>();
        services.TryAddSingleton<IServiceRevisionCatalogQueryReader, ServiceRevisionCatalogQueryReader>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceCatalogProjectionContext, IReadOnlyList<string>>,
            ServiceCatalogProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceRevisionCatalogProjectionContext, IReadOnlyList<string>>,
            ServiceRevisionCatalogProjector>());

        return services;
    }
}
