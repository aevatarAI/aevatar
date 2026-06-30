using System.Diagnostics.Metrics;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

// Test-add (06-20-observatory-run-state-feed / R2b): the reclaim metric records exactly one counter
// measurement per gate decision, tagged so dashboards can split confirmed vs deferred reclaims and, for
// deferrals, why (unknown_head_version / watermark_unreached). RecordConfirmed always pairs outcome=confirmed
// with reason=materialized; RecordDeferred carries outcome=deferred plus the caller-supplied reason verbatim.
public sealed class WorkflowRunReclaimMetricsTests
{
    [Fact]
    public void RecordConfirmed_ShouldEmitOneConfirmedMaterializedMeasurement()
    {
        using var capture = new ReclaimMetricCapture();

        WorkflowRunReclaimMetrics.RecordConfirmed();

        var measurement = capture.Measurements.Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        measurement.Tag(WorkflowRunReclaimMetrics.OutcomeTag).Should().Be(WorkflowRunReclaimMetrics.OutcomeConfirmed);
        measurement.Tag(WorkflowRunReclaimMetrics.ReasonTag).Should().Be(WorkflowRunReclaimMetrics.ReasonMaterialized);
    }

    [Theory]
    [InlineData(WorkflowRunReclaimMetrics.ReasonUnknownHeadVersion)]
    [InlineData(WorkflowRunReclaimMetrics.ReasonWatermarkUnreached)]
    public void RecordDeferred_ShouldEmitOneDeferredMeasurementCarryingTheReason(string reason)
    {
        using var capture = new ReclaimMetricCapture();

        WorkflowRunReclaimMetrics.RecordDeferred(reason);

        var measurement = capture.Measurements.Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        measurement.Tag(WorkflowRunReclaimMetrics.OutcomeTag).Should().Be(WorkflowRunReclaimMetrics.OutcomeDeferred);
        measurement.Tag(WorkflowRunReclaimMetrics.ReasonTag).Should().Be(reason);
    }

    [Fact]
    public void RecordDeferred_ShouldPassTheReasonThroughVerbatimWithoutInterpretingIt()
    {
        using var capture = new ReclaimMetricCapture();

        WorkflowRunReclaimMetrics.RecordDeferred("some_future_reason");

        capture.Measurements.Should().ContainSingle()
            .Which.Tag(WorkflowRunReclaimMetrics.ReasonTag).Should().Be("some_future_reason");
    }

    [Fact]
    public void RecordOutcomes_ShouldAccumulateOneMeasurementPerCallOnTheSameCounter()
    {
        using var capture = new ReclaimMetricCapture();

        WorkflowRunReclaimMetrics.RecordConfirmed();
        WorkflowRunReclaimMetrics.RecordDeferred(WorkflowRunReclaimMetrics.ReasonWatermarkUnreached);
        WorkflowRunReclaimMetrics.RecordConfirmed();

        capture.Measurements.Should().HaveCount(3);
        capture.Measurements.Should().OnlyContain(m => m.Value == 1);
        capture.Measurements
            .Count(m => string.Equals(m.Tag(WorkflowRunReclaimMetrics.OutcomeTag), WorkflowRunReclaimMetrics.OutcomeConfirmed, StringComparison.Ordinal))
            .Should().Be(2);
        capture.Measurements
            .Count(m => string.Equals(m.Tag(WorkflowRunReclaimMetrics.OutcomeTag), WorkflowRunReclaimMetrics.OutcomeDeferred, StringComparison.Ordinal))
            .Should().Be(1);
    }

    // Subscribes to the reclaim meter and records every counter measurement (value + tags) for assertion.
    private sealed class ReclaimMetricCapture : IDisposable
    {
        private const string ReclaimMeterName = "Aevatar.Workflow.Run.Reclaim";
        private const string ReclaimCounterName = "aevatar.workflow.run_reclaim_outcomes_total";
        private readonly MeterListener _listener = new();

        public ReclaimMetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ReclaimMeterName)
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (instrument.Name == ReclaimCounterName)
                    Measurements.Add(new MetricMeasurement(measurement, tags.ToArray()));
            });

            _listener.Start();
        }

        public List<MetricMeasurement> Measurements { get; } = [];

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    private sealed record MetricMeasurement(long Value, KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string key) => Tags.FirstOrDefault(t => t.Key == key).Value as string;
    }
}
