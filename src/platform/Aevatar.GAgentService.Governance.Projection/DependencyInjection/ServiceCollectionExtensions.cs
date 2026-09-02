using Aevatar.Audit.Abstractions.CommittedFacts;
using Aevatar.Audit.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Projection.Audit;
using Aevatar.GAgentService.Governance.Projection.Configuration;
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
    public static IServiceCollection AddGAgentServiceGovernanceProjection(
        this IServiceCollection services,
        Action<ServiceGovernanceProjectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ServiceGovernanceProjectionOptions();
        configure?.Invoke(options);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();

        services.AddProjectionMaterializationRuntimeCore<
            ServiceConfigurationProjectionContext,
            ServiceConfigurationRuntimeLease,
            ProjectionMaterializationScopeGAgent<ServiceConfigurationProjectionContext>>(
            scopeKey => new ServiceConfigurationProjectionContext
            {
                RootActorId = scopeKey.RootActorId,
                ProjectionKind = scopeKey.ProjectionKind,
            },
            context => new ServiceConfigurationRuntimeLease(context));
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            ServiceGovernanceCommittedStateProjectionActivationPlanProvider>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ServiceConfigurationReadModel>, ServiceConfigurationReadModelMetadataProvider>();
        services.TryAddSingleton<IServiceConfigurationQueryReader, ServiceConfigurationQueryReader>();
        services.AddProjectionArtifactMaterializer<
            ServiceConfigurationProjectionContext,
            ServiceConfigurationProjector>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceBindingCreatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceBindingUpdatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceBindingRetiredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceEndpointCatalogCreatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServiceEndpointCatalogUpdatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServicePolicyCreatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServicePolicyUpdatedAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, ServicePolicyRetiredAuditTranslator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditCommittedEventTranslator, LegacyServiceConfigurationImportedAuditTranslator>());
        services.AddAuditCommittedFactMaterializer<ServiceConfigurationProjectionContext>();

        return services;
    }
}
