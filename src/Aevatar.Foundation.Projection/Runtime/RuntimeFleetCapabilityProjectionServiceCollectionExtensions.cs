using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.DependencyInjection;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Foundation.Projection.Runtime;

public static class RuntimeFleetCapabilityProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddRuntimeFleetCapabilityProjection(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddProjectionReadModelRuntime();
        services.TryAddSingleton<IProjectionClock, SystemProjectionClock>();
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<
            RuntimeFleetCapabilityAuthorityCurrentStateDocument>,
            RuntimeFleetCapabilityAuthorityCurrentStateDocumentMetadataProvider>();
        services.AddProjectionMaterializationRuntimeCore<
            RuntimeFleetCapabilityProjectionContext,
            RuntimeFleetCapabilityProjectionRuntimeLease,
            ProjectionMaterializationScopeGAgent<RuntimeFleetCapabilityProjectionContext>>(
            scope => new RuntimeFleetCapabilityProjectionContext
            {
                RootActorId = scope.RootActorId,
                ProjectionKind = scope.ProjectionKind,
            },
            context => new RuntimeFleetCapabilityProjectionRuntimeLease(context));
        services.AddCurrentStateProjectionMaterializer<
            RuntimeFleetCapabilityProjectionContext,
            RuntimeFleetCapabilityAuthorityCurrentStateProjector>();
        services.TryAddSingleton<ProjectionActivationPlanDispatcher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            CommittedStateProjectionActivationHook>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IProjectionActivationPlanProvider,
            RuntimeFleetCapabilityCommittedStateProjectionActivationPlanProvider>());
        services.TryAddSingleton<ProjectionRuntimeFleetCapabilityAdmissionReader>();
        services.Replace(ServiceDescriptor.Singleton<IRuntimeFleetCapabilityAdmissionReader>(
            static provider => provider.GetRequiredService<ProjectionRuntimeFleetCapabilityAdmissionReader>()));
        services.Replace(ServiceDescriptor.Singleton<IRuntimeFleetCapabilityQuiescenceReader>(
            static provider => provider.GetRequiredService<ProjectionRuntimeFleetCapabilityAdmissionReader>()));
        return services;
    }
}
