using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aevatar.AI.Core.Observability;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Observability;

public sealed class AgentProfileTelemetryTests
{
    [Fact]
    public void Four_seams_should_emit_allowlisted_metrics_and_binding_trace_fields()
    {
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentProfileTelemetry.MeterName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(activityListener);
        var measurements = new List<(string Instrument, KeyValuePair<string, object?>[] Tags)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentProfileTelemetry.MeterName &&
                instrument.Name.StartsWith("aevatar.agent_profile", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray())));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray())));
        meterListener.Start();

        var context = BuildContext();
        using (AgentProfileTelemetry.StartTurn(context))
        {
            AgentProfileTelemetry.RecordRouteDecision(context, "classifier", "intent-a", "none", "rule-a", 12);
            AgentProfileTelemetry.RecordSelectedSkill(context, "skill-guid", "1.2");
            AgentProfileTelemetry.RecordPromptAndToolMaterialization(context, "selected", 512, 4, "ok");
            AgentProfileTelemetry.RecordPlanOrHandoff(context, "pending", 1, 0);
            AgentProfileTelemetry.RecordFirstStreamedOutput(context, "ok", 80);
        }

        measurements.SelectMany(static measurement => measurement.Tags)
            .Select(static tag => tag.Key)
            .Distinct()
            .Should()
            .BeSubsetOf([
                "aevatar.agent_profile.seam",
                "aevatar.agent_profile.activation_mode",
                "aevatar.agent_profile.outcome",
                "aevatar.agent_profile.size_kind",
            ]);
        measurements.SelectMany(static measurement => measurement.Tags)
            .Select(static tag => tag.Value?.ToString())
            .Should()
            .Contain(["route", "materialize", "plan_handoff", "first_stream_output"])
            .And.NotContain("exact_fetch");
        activities.Should().ContainSingle();
        activities[0].TagObjects.Should().Contain(pair =>
            pair.Key == "aevatar.agent_profile.source_state_version" && Equals(pair.Value, 17L));
        activities[0].TagObjects.Should().Contain(pair =>
            pair.Key == "aevatar.agent_profile.published_revision" && Equals(pair.Value, 5L));
        activities[0].Tags.Should().Contain(pair =>
            pair.Key == "aevatar.agent_profile.published_snapshot_sha256" && pair.Value == "published-hash");
        activities[0].Tags.Should().Contain(pair =>
            pair.Key == "aevatar.agent_profile.execution_binding_sha256" && pair.Value == "binding-hash");
        activities[0].Tags.Should().Contain(pair =>
            pair.Key == "aevatar.agent_profile.rollout.release" && pair.Value == "nyxid-chat-r7");
        activities[0].Tags.Should().Contain(pair =>
            pair.Key == "aevatar.agent_profile.rollout.stage" && pair.Value == "canary");
        activities[0].Tags.Should().Contain(pair =>
            pair.Key == "aevatar.agent_profile.selected_skill.guid" && pair.Value == "skill-guid");
    }

    [Fact]
    public void Public_telemetry_surface_should_not_accept_content_arguments_or_sensitive_tag_names()
    {
        var methods = typeof(AgentProfileTelemetry).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

        methods.SelectMany(static method => method.GetParameters())
            .Select(static parameter => parameter.Name)
            .Where(static name => name != null &&
                (name.Contains("content", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("header", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("argument", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("secret", StringComparison.OrdinalIgnoreCase)))
            .Should()
            .BeEmpty();
    }

    private static AgentProfileTelemetryContext BuildContext() => new(
        "profile-alpha",
        17,
        5,
        "published-hash",
        "binding-hash",
        "shadow",
        "nyxid-chat-r7",
        "canary");
}
