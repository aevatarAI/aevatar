using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.DependencyInjection;

// Refactor (iter17/cluster-034):
//   Old pattern: Replay-based projection scope watermark query via IEventStore (EventStoreProjectionScopeWatermarkQueryPort).
//   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays IEventStore.
public static class ProjectionScopeStatusRuntimeRegistration
{
    public static IServiceCollection AddProjectionScopeStatusRuntimeCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAevatarAgentKindRegistry(builder =>
        {
            builder.Register(ProjectionScopeAgentRegistration
                .Create<ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>>());
            // Terminal status materializer (#3476): its own kind and protobuf state, activated by
            // the source scope actor once the fleet admits the terminal contract.
            builder.Register(ProjectionScopeAgentRegistration.Create<ProjectionScopeStatusGAgent>());
        });
        // Every silo that can activate the terminal materializer advertises its contract; the
        // fleet authority opens the terminal gate only when all active silos do.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IRuntimeFleetCapabilityAdvertisement,
            ProjectionScopeStatusTerminalCapabilityAdvertisement>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IRuntimeFleetCapabilityAdvertisement,
            ProjectionScopeStatusTerminalActivationSealCapabilityAdvertisement>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ICommittedStatePublicationHook,
            ProjectionScopeCommittedStateRedactionHook>());
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ProjectionScopeStatusDocument>,
            ProjectionScopeStatusDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionScopeWatermarkQueryPort, ProjectionScopeStatusQueryPort>();
        services.TryAddSingleton<IProjectionScopeStatusListQueryPort, ProjectionScopeStatusListQueryPort>();
        services.TryAddSingleton<IProjectionScopeIntrospectionQueryPort, ProjectionScopeIntrospectionQueryPort>();
        services.TryAddSingleton<IProjectionFailureReplayService, ProjectionFailureReplayService>();
        services.TryAddSingleton<ProjectionFailureRecoveryReconciler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProjectionFailureRecoveryHostedService>());
        services.AddCurrentStateProjectionMaterializer<
            ProjectionScopeStatusMaterializationContext,
            ProjectionScopeStatusProjector>();
        services.TryAddSingleton<Func<ProjectionRuntimeScopeKey, ProjectionScopeStatusMaterializationContext>>(
            static _ => static scopeKey => new ProjectionScopeStatusMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
            });
        return services;
    }

    internal static bool IsProjectionScopeStatusKind(string? projectionKind) =>
        string.Equals(
            projectionKind,
            ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            StringComparison.Ordinal) ||
        string.Equals(
            projectionKind,
            ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            StringComparison.Ordinal);
}
