using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Projection.Contexts;
using Aevatar.GAgentService.Governance.Projection.Metadata;
using Aevatar.GAgentService.Governance.Projection.Orchestration;
using Aevatar.GAgentService.Governance.Projection.Projectors;
using Aevatar.GAgentService.Governance.Projection.Queries;
using Aevatar.GAgentService.Governance.Projection.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgentService.Governance.Projection.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGAgentServiceGovernanceProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton(typeof(IActorStreamSubscriptionHub<>), typeof(ActorStreamSubscriptionHub<>));

        services.AddEventSinkProjectionRuntimeCore<
            ServiceConfigurationProjectionContext,
            IReadOnlyList<string>,
            ServiceConfigurationRuntimeLease,
            EventEnvelope>();

        services.TryAddSingleton<IProjectionPortActivationService<ServiceConfigurationRuntimeLease>, ServiceConfigurationProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceConfigurationRuntimeLease>, ServiceConfigurationProjectionReleaseService>();
        services.TryAddSingleton<ServiceConfigurationProjectionPortService>();
        services.TryAddSingleton<IServiceConfigurationProjectionPort>(sp => sp.GetRequiredService<ServiceConfigurationProjectionPortService>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceConfigurationReadModel>, ServiceConfigurationReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceConfigurationQueryReader, ServiceConfigurationQueryReader>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceConfigurationProjectionContext, IReadOnlyList<string>>,
            ServiceConfigurationProjector>());

        return services;
    }
}
