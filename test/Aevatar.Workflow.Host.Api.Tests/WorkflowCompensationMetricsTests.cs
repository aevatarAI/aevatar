using System.Diagnostics.Metrics;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Projection.Observability;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowCompensationMetricsTests
{
    [Fact]
    public void ObserveCommittedPayload_ShouldRecordCompensationOutcomeCounters()
    {
        using var capture = new WorkflowCompensationMetricCapture();

        WorkflowCompensationMetrics.ObserveCommittedPayload(Any.Pack(new CompensationRequestEvent()));
        WorkflowCompensationMetrics.ObserveCommittedPayload(Any.Pack(new CompensationStepCompletedEvent
        {
            Success = true,
        }));
        WorkflowCompensationMetrics.ObserveCommittedPayload(Any.Pack(new CompensationStepCompletedEvent
        {
            Success = false,
        }));
        WorkflowCompensationMetrics.ObserveCommittedPayload(Any.Pack(new WorkflowCompensationFailedEvent()));

        capture.RequestedMeasurements.Should().ContainSingle().Which.Value.Should().Be(1);
        var succeeded = capture.SucceededMeasurements.Should().ContainSingle().Subject;
        succeeded.Value.Should().Be(1);
        succeeded.Tags.Should().ContainSingle(tag =>
            tag.Key == WorkflowCompensationMetrics.OutcomeTag &&
            Equals(tag.Value, WorkflowCompensationMetrics.OutcomeSucceeded));
        capture.DeadLetteredMeasurements.Should().ContainSingle().Which.Value.Should().Be(1);
    }

    private sealed class WorkflowCompensationMetricCapture : IDisposable
    {
        private const string WorkflowMeterName = "Aevatar.Workflow";
        private readonly MeterListener _listener = new();

        public WorkflowCompensationMetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == WorkflowMeterName)
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                var captured = new MetricMeasurement(measurement, tags.ToArray());
                if (instrument.Name == WorkflowCompensationMetrics.RequestedMetricName)
                    RequestedMeasurements.Add(captured);
                else if (instrument.Name == WorkflowCompensationMetrics.SucceededMetricName)
                    SucceededMeasurements.Add(captured);
                else if (instrument.Name == WorkflowCompensationMetrics.DeadLetteredMetricName)
                    DeadLetteredMeasurements.Add(captured);
            });

            _listener.Start();
        }

        public List<MetricMeasurement> RequestedMeasurements { get; } = [];
        public List<MetricMeasurement> SucceededMeasurements { get; } = [];
        public List<MetricMeasurement> DeadLetteredMeasurements { get; } = [];

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    private sealed record MetricMeasurement(long Value, KeyValuePair<string, object?>[] Tags);
}
