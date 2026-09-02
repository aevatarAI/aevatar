using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aevatar.AI.Core.Observability;

public sealed record AgentProfileTelemetryContext(
    string ProfileId,
    string ProfileVersion,
    string PolicyRevision,
    string PolicySha256,
    string ActivationMode,
    string SkillsetGuid,
    string SkillsetLiteralVersion);

public static class AgentProfileTelemetry
{
    public const string MeterName = "Aevatar.GenAI";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> SeamEvents = Meter.CreateCounter<long>(
        "aevatar.agent_profile.seam.events",
        description: "Agent-profile rollout observations at fixed execution seams.");
    private static readonly Histogram<double> SeamDuration = Meter.CreateHistogram<double>(
        "aevatar.agent_profile.seam.duration",
        unit: "ms",
        description: "Bounded latency at fixed agent-profile execution seams.");
    private static readonly Histogram<long> MaterializedSize = Meter.CreateHistogram<long>(
        "aevatar.agent_profile.materialized.size",
        unit: "{item}",
        description: "Prompt bytes and effective tool counts without content.");

    public static Activity? StartTurn(AgentProfileTelemetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var activity = GenAIActivitySource.Source.StartActivity("agent_profile turn", ActivityKind.Internal);
        if (activity is null)
            return null;
        SetProfileTags(activity, context);
        return activity;
    }

    public static void RecordRouteDecision(
        AgentProfileTelemetryContext context,
        string routingMode,
        string intentId,
        string degradation,
        string matchedRuleId,
        double durationMs)
    {
        SetProfileTags(Activity.Current, context);
        Activity.Current?.SetTag("aevatar.agent_profile.routing.mode", routingMode);
        Activity.Current?.SetTag("aevatar.agent_profile.intent.id", intentId);
        Activity.Current?.SetTag("aevatar.agent_profile.routing.degradation", degradation);
        Activity.Current?.SetTag("aevatar.agent_profile.routing.matched_rule_id", matchedRuleId);
        RecordSeam("route", context.ActivationMode, degradation, durationMs);
    }

    public static void RecordExactFetch(
        AgentProfileTelemetryContext context,
        string selectedSkillGuid,
        string selectedSkillLiteralVersion,
        string outcome,
        double durationMs)
    {
        SetProfileTags(Activity.Current, context);
        Activity.Current?.SetTag("aevatar.agent_profile.selected_skill.guid", selectedSkillGuid);
        Activity.Current?.SetTag("aevatar.agent_profile.selected_skill.literal_version", selectedSkillLiteralVersion);
        RecordSeam("exact_fetch", context.ActivationMode, outcome, durationMs);
    }

    public static void RecordPromptAndToolMaterialization(
        AgentProfileTelemetryContext context,
        string layerProvenance,
        int selectedSkillBytes,
        int effectiveToolCount,
        string outcome)
    {
        SetProfileTags(Activity.Current, context);
        Activity.Current?.SetTag("aevatar.agent_profile.layer.provenance", layerProvenance);
        Activity.Current?.SetTag("aevatar.agent_profile.selected_skill.bytes", selectedSkillBytes);
        Activity.Current?.SetTag("aevatar.agent_profile.effective_tool.count", effectiveToolCount);
        var tags = MetricTags("materialize", context.ActivationMode, outcome);
        SeamEvents.Add(1, tags);
        MaterializedSize.Record(selectedSkillBytes, tags.With("aevatar.agent_profile.size_kind", "prompt_bytes"));
        MaterializedSize.Record(effectiveToolCount, tags.With("aevatar.agent_profile.size_kind", "tool_count"));
    }

    public static void RecordPlanOrHandoff(
        AgentProfileTelemetryContext context,
        string status,
        int planStep,
        int ordinaryRecoveryCount)
    {
        SetProfileTags(Activity.Current, context);
        Activity.Current?.SetTag("aevatar.agent_profile.plan.status", status);
        Activity.Current?.SetTag("aevatar.agent_profile.plan.step", planStep);
        Activity.Current?.SetTag("aevatar.agent_profile.recovery.count", ordinaryRecoveryCount);
        SeamEvents.Add(1, MetricTags("plan_handoff", context.ActivationMode, status));
    }

    public static void RecordFirstStreamedOutput(
        AgentProfileTelemetryContext context,
        string outcome,
        double durationMs)
    {
        SetProfileTags(Activity.Current, context);
        RecordSeam("first_stream_output", context.ActivationMode, outcome, durationMs);
    }

    private static void RecordSeam(string seam, string activationMode, string outcome, double durationMs)
    {
        var tags = MetricTags(seam, activationMode, outcome);
        SeamEvents.Add(1, tags);
        SeamDuration.Record(durationMs, tags);
    }

    private static TagList MetricTags(string seam, string activationMode, string outcome) =>
        new()
        {
            { "aevatar.agent_profile.seam", seam },
            { "aevatar.agent_profile.activation_mode", activationMode },
            { "aevatar.agent_profile.outcome", outcome },
        };

    private static void SetProfileTags(Activity? activity, AgentProfileTelemetryContext context)
    {
        if (activity is null)
            return;
        activity.SetTag("aevatar.agent_profile.id", context.ProfileId);
        activity.SetTag("aevatar.agent_profile.version", context.ProfileVersion);
        activity.SetTag("aevatar.agent_profile.policy_revision", context.PolicyRevision);
        activity.SetTag("aevatar.agent_profile.policy_sha256", context.PolicySha256);
        activity.SetTag("aevatar.agent_profile.activation_mode", context.ActivationMode);
        activity.SetTag("aevatar.agent_profile.skillset.guid", context.SkillsetGuid);
        activity.SetTag("aevatar.agent_profile.skillset.literal_version", context.SkillsetLiteralVersion);
    }

    private static TagList With(this TagList tags, string name, object value)
    {
        tags.Add(name, value);
        return tags;
    }
}
