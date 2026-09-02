using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Aevatar.Foundation.Runtime.Observability;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeEnvelopeTerminalFailureMetricsTests
{
    [Fact]
    public void RecordEnvelopeTerminalFailure_ShouldExposeReasonAndFailureDisposition()
    {
        var measurements = new ConcurrentQueue<(long Value, string? Reason, string? Disposition)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == AgentMetrics.RuntimeEnvelopeTerminalFailuresMetricName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? reason = null;
            string? disposition = null;
            foreach (var tag in tags)
            {
                if (tag.Key == AgentMetrics.FailureReasonTag)
                    reason = tag.Value?.ToString();
                else if (tag.Key == AgentMetrics.FailureDispositionTag)
                    disposition = tag.Value?.ToString();
            }

            measurements.Enqueue((value, reason, disposition));
        });
        listener.Start();

        AgentMetrics.RecordEnvelopeTerminalFailure(
            AgentMetrics.FailureReasonHandlerRetryExhausted,
            AgentMetrics.FailureDispositionReturned);

        measurements.Should().Contain(measurement =>
            measurement.Value == 1 &&
            measurement.Reason == AgentMetrics.FailureReasonHandlerRetryExhausted &&
            measurement.Disposition == AgentMetrics.FailureDispositionReturned);
    }
}
