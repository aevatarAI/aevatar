using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Projection port that activates the materialization scope for a per-target
/// <see cref="HealthProbeTargetGAgent"/>. Activation is owned by committed-state
/// hooks or projection lifecycle orchestration, not by the startup service.
/// </summary>
// Refactor (iter47/cluster-005-status-dashboard-startup-projection-activation):
//   Old pattern: Startup service explicitly ensures projection scopes and uses Task.Delay retry before dispatching configure commands.
//   New principle: Startup path dispatches actor configuration only; projection activation owned by committed-state hooks; retry uses hosted-service scheduling.
public sealed class HealthProbeProjectionPort
    : MaterializationProjectionPortBase<HealthProbeMaterializationRuntimeLease>
{
    public const string ProjectionKind = "health-probe-target";

    public HealthProbeProjectionPort(
        IProjectionScopeActivationService<HealthProbeMaterializationRuntimeLease> activationService)
        : base(static () => true, activationService)
    {
    }

    public Task<HealthProbeMaterializationRuntimeLease?> EnsureProjectionForActorAsync(
        string actorId,
        CancellationToken ct = default) =>
        EnsureProjectionAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
            ct);
}
