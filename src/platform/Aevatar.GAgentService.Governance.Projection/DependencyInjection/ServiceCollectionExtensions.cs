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
            ServiceBindingProjectionContext,
            IReadOnlyList<string>,
            ServiceBindingRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            ServiceEndpointCatalogProjectionContext,
            IReadOnlyList<string>,
            ServiceEndpointCatalogRuntimeLease,
            EventEnvelope>();
        services.AddEventSinkProjectionRuntimeCore<
            ServicePolicyProjectionContext,
            IReadOnlyList<string>,
            ServicePolicyRuntimeLease,
            EventEnvelope>();

        services.TryAddSingleton<IProjectionPortActivationService<ServiceBindingRuntimeLease>, ServiceBindingProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceBindingRuntimeLease>, ServiceBindingProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortActivationService<ServiceEndpointCatalogRuntimeLease>, ServiceEndpointCatalogProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServiceEndpointCatalogRuntimeLease>, ServiceEndpointCatalogProjectionReleaseService>();
        services.TryAddSingleton<IProjectionPortActivationService<ServicePolicyRuntimeLease>, ServicePolicyProjectionActivationService>();
        services.TryAddSingleton<IProjectionPortReleaseService<ServicePolicyRuntimeLease>, ServicePolicyProjectionReleaseService>();
        services.TryAddSingleton<ServiceBindingProjectionPortService>();
        services.TryAddSingleton<ServiceEndpointCatalogProjectionPortService>();
        services.TryAddSingleton<ServicePolicyProjectionPortService>();
        services.TryAddSingleton<IServiceBindingProjectionPort>(sp => sp.GetRequiredService<ServiceBindingProjectionPortService>());
        services.TryAddSingleton<IServiceEndpointCatalogProjectionPort>(sp => sp.GetRequiredService<ServiceEndpointCatalogProjectionPortService>());
        services.TryAddSingleton<IServicePolicyProjectionPort>(sp => sp.GetRequiredService<ServicePolicyProjectionPortService>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceBindingCatalogReadModel>, ServiceBindingCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceEndpointCatalogReadModel>, ServiceEndpointCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServicePolicyCatalogReadModel>, ServicePolicyCatalogReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceBindingQueryReader, ServiceBindingQueryReader>();
        services.TryAddSingleton<IServiceEndpointCatalogQueryReader, ServiceEndpointCatalogQueryReader>();
        services.TryAddSingleton<IServicePolicyQueryReader, ServicePolicyQueryReader>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceBindingProjectionContext, IReadOnlyList<string>>,
            ServiceBindingProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServiceEndpointCatalogProjectionContext, IReadOnlyList<string>>,
            ServiceEndpointCatalogProjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionProjector<ServicePolicyProjectionContext, IReadOnlyList<string>>,
            ServicePolicyProjector>());

        return services;
    }
}
