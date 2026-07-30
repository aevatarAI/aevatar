using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            builder.Register(ProjectionScopeAgentRegistration
                .Create<ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>>()));
        services.TryAddSingleton<IProjectionDocumentMetadataProvider<ProjectionScopeStatusDocument>,
            ProjectionScopeStatusDocumentMetadataProvider>();
        services.TryAddSingleton<IProjectionScopeWatermarkQueryPort, ProjectionScopeStatusQueryPort>();
        services.TryAddSingleton<IProjectionScopeStatusListQueryPort, ProjectionScopeStatusListQueryPort>();
        services.TryAddSingleton<IProjectionScopeIntrospectionQueryPort, ProjectionScopeIntrospectionQueryPort>();
        services.AddCurrentStateProjectionMaterializer<
            ProjectionScopeStatusMaterializationContext,
            ProjectionScopeStatusProjector>();
        services.TryAddSingleton<Func<ProjectionRuntimeScopeKey, ProjectionScopeStatusMaterializationContext>>(
            static _ => static scopeKey => new ProjectionScopeStatusMaterializationContext
            {
                RootActorId = scopeKey.RootActorId,
            });
        services.TryAddSingleton<Func<ProjectionScopeStatusMaterializationContext, ProjectionScopeStatusRuntimeLease>>(
            static _ => static context => new ProjectionScopeStatusRuntimeLease(context));
        services.TryAddSingleton<IProjectionScopeAttachExistingLeaseLookup<ProjectionScopeStatusRuntimeLease>>(sp =>
            new ProjectionScopeAttachExistingLeaseLookup<
                ProjectionScopeStatusRuntimeLease,
                ProjectionScopeStatusMaterializationContext>(
                sp.GetRequiredService<IActorRuntime>(),
                request => new ProjectionScopeStatusMaterializationContext
                {
                    RootActorId = request.RootActorId,
                },
                (_, context) => new ProjectionScopeStatusRuntimeLease(context)));
        services.TryAddSingleton<IProjectionScopeActivationService<ProjectionScopeStatusRuntimeLease>>(sp =>
            new ProjectionScopeActivationService<
                ProjectionScopeStatusRuntimeLease,
                ProjectionScopeStatusMaterializationContext,
                ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                request => new ProjectionScopeStatusMaterializationContext
                {
                    RootActorId = request.RootActorId,
                },
                (_, context) => new ProjectionScopeStatusRuntimeLease(context),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindVerifier>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>(),
                sp.GetService<IStreamPubSubMaintenance>(),
                sp.GetService<ILoggerFactory>()));
        services.TryAddSingleton<IProjectionScopeReleaseService<ProjectionScopeStatusRuntimeLease>>(sp =>
            new ProjectionScopeReleaseService<
                ProjectionScopeStatusRuntimeLease,
                ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>>(
                sp.GetRequiredService<IActorRuntime>(),
                sp.GetRequiredService<IActorDispatchPort>(),
                lease => new ProjectionRuntimeScopeKey(
                    lease.Context.RootActorId,
                    lease.Context.ProjectionKind,
                    ProjectionRuntimeMode.DurableMaterialization),
                sp.GetService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindVerifier>(),
                sp.GetRequiredService<Aevatar.Foundation.Abstractions.TypeSystem.IAgentKindRegistry>()));
        return services;
    }

    internal static bool IsProjectionScopeStatusKind(string? projectionKind) =>
        string.Equals(
            projectionKind,
            ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            StringComparison.Ordinal);

    internal static ProjectionScopeStartRequest BuildStatusScopeStartRequest(ProjectionScopeStartRequest sourceRequest) =>
        new()
        {
            RootActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
                sourceRequest.RootActorId ?? string.Empty,
                sourceRequest.ProjectionKind ?? string.Empty,
                ProjectionRuntimeMode.DurableMaterialization,
                sourceRequest.SessionId)),
            ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            Mode = ProjectionRuntimeMode.DurableMaterialization,
        };
}
