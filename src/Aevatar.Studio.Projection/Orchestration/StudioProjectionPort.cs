using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Studio.Projection.Orchestration;

/// <summary>
/// Activates the Studio materialization scope for a given Studio actor so
/// the <c>ProjectionMaterializationScopeGAgent&lt;StudioMaterializationContext&gt;</c>
/// subscribes to its event stream and hands every committed event to the
/// registered Studio projectors. Without this activation the actor writes
/// fine but nothing gets materialized into the read-model document store —
/// every GET then returns defaults and "persisted" data appears to vanish
/// on refresh.
///
/// Mirror of
/// <c>Aevatar.GAgentService.Governance.Projection.Orchestration.ServiceConfigurationProjectionPort</c>
/// and <c>Aevatar.GAgents.Channel.Runtime.ChannelBotRegistrationProjectionPort</c>,
/// applied here to the Studio runtime lease.
/// </summary>
public sealed class StudioProjectionPort
    : MaterializationProjectionPortBase<StudioMaterializationRuntimeLease>
{
    public StudioProjectionPort(
        IProjectionScopeActivationService<StudioMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<StudioMaterializationRuntimeLease?> EnsureProjectionAsync(
        string actorId,
        string projectionKind,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return Task.FromResult<StudioMaterializationRuntimeLease?>(null);
        if (string.IsNullOrWhiteSpace(projectionKind))
            return Task.FromResult<StudioMaterializationRuntimeLease?>(null);

        return EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
    }
}

