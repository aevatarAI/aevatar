using System.Diagnostics.Metrics;

namespace Aevatar.Workflow.Application.Runs;

// 06-20-observatory-run-state-feed (R2b): observability for the gated reclaim of throwaway ad-hoc run
// actors. A "deferred" outcome means the run+definition actors were intentionally NOT destroyed because
// materialization could not be confirmed (timeout / scope absent / unknown head version); those stragglers
// persist like deployed runs and are documented debt pending a uniform run-actor retention/GC follow-up.
internal static class WorkflowRunReclaimMetrics
{
    private static readonly Meter Meter = new("Aevatar.Workflow.Run.Reclaim", "1.0.0");

    internal const string OutcomeTag = "outcome";
    internal const string ReasonTag = "reason";

    internal const string OutcomeConfirmed = "confirmed";
    internal const string OutcomeDeferred = "deferred";

    internal const string ReasonMaterialized = "materialized";
    internal const string ReasonUnknownHeadVersion = "unknown_head_version";
    internal const string ReasonWatermarkUnreached = "watermark_unreached";

    public static readonly Counter<long> ReclaimOutcomesTotal = Meter.CreateCounter<long>(
        "aevatar.workflow.run_reclaim_outcomes_total",
        description: "Workflow ad-hoc run reclaim gate outcomes by outcome and reason.");

    public static void RecordConfirmed() =>
        ReclaimOutcomesTotal.Add(1, [new(OutcomeTag, OutcomeConfirmed), new(ReasonTag, ReasonMaterialized)]);

    public static void RecordDeferred(string reason) =>
        ReclaimOutcomesTotal.Add(1, [new(OutcomeTag, OutcomeDeferred), new(ReasonTag, reason)]);
}
