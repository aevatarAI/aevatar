using Aevatar.Foundation.Abstractions.Maintenance;

namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Retires the stale health-probe projection-scope actor shape that persisted
/// the probe target actor kind before projection scopes moved to explicit
/// materialization-scope kinds.
/// </summary>
public sealed class StatusDashboardRetiredActorSpec : RetiredActorSpec
{
    private const string RetiredHealthProbeTargetActorKind = "status.dashboard.health-probe-target";

    public override string SpecId => "status-dashboard";

    public override IReadOnlyList<RetiredActorTarget> Targets { get; } =
    [
        new(
            "projection.durable.scope:health-probe-target:health-probe::aevatar-core-loop-tools",
            [RetiredHealthProbeTargetActorKind],
            SourceStreamId: "health-probe::aevatar-core-loop-tools"),
    ];
}
