using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aevatar.AI.Core.Observability;
using FluentAssertions;

namespace Aevatar.AI.Core.Tests.Observability;

public sealed class AgentProfileTelemetryTests
{
    [Fact]
    public void Five_seams_should_emit_allowlisted_metrics_and_bounded_trace_fields()
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
            AgentProfileTelemetry.RecordExactFetch(context, "skill-guid", "1.2", "ok", 25);
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
            .Contain(["route", "exact_fetch", "materialize", "plan_handoff", "first_stream_output"]);
        activities.Should().ContainSingle();
        activities[0].Tags.Should().Contain(pair =>
            pair.Key == "aevatar.agent_profile.policy_sha256" && pair.Value == "policy-hash");
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
        "nyxid-chat",
        "shadow-v1",
        "policy-v1",
        "policy-hash",
        "shadow",
        "skillset-guid",
        "1.0");
}
