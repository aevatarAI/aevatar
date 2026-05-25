using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Projection port that activates the materialization scope for a per-target
/// <see cref="HealthProbeTargetGAgent"/>. Startup service primes one scope per
/// configured probe target so the current-state read model is rebuilt after
/// restart without query-path priming.
/// </summary>
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
