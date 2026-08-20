using System.Diagnostics.Metrics;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Core.Tests.Modules;

public sealed class WorkflowToolCallTelemetryTests
{
    [Fact]
    public void Record_ShouldEmitOnlyBoundedMetricTagsAndPayloadFreeStructuredLog()
    {
        var measurements = new List<Measurement>();
        using var listener = Listen(measurements);
        var logger = new RecordingLogger();
        var observation = Observation();

        WorkflowToolCallTelemetry.Record(logger, observation);

        measurements.Should().HaveCount(2);
        measurements.Select(measurement => measurement.Instrument).Should().BeEquivalentTo(
            WorkflowToolCallTelemetry.WaterlineTotalMetricName,
            WorkflowToolCallTelemetry.PhaseDurationMetricName);
        var allowedKeys = new[]
        {
            WorkflowToolCallTelemetry.WaterlineTag,
            WorkflowToolCallTelemetry.PhaseTag,
            WorkflowToolCallTelemetry.DispositionTag,
            WorkflowToolCallTelemetry.DeliveryMethodTag,
        };
        measurements.Should().OnlyContain(measurement =>
            measurement.Tags.Keys.SequenceEqual(allowedKeys));
        measurements.SelectMany(measurement => measurement.Tags.Keys).Should().NotContain(key =>
            key.Contains("id", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("scope", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("run", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("step", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("call", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("execution", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("continuation", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("dispatch", StringComparison.OrdinalIgnoreCase));
        measurements.Should().OnlyContain(measurement =>
            Equals(measurement.Tags[WorkflowToolCallTelemetry.WaterlineTag], "provider_returned") &&
            Equals(measurement.Tags[WorkflowToolCallTelemetry.PhaseTag], "external_execution") &&
            Equals(measurement.Tags[WorkflowToolCallTelemetry.DispositionTag], "succeeded") &&
            Equals(measurement.Tags[WorkflowToolCallTelemetry.DeliveryMethodTag], "none"));

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Properties["ScopeId"].Should().Be("scope-alpha");
        entry.Properties["RunId"].Should().Be("run-alpha");
        entry.Properties["StepId"].Should().Be("step-alpha");
        entry.Properties["CallId"].Should().Be("call-alpha");
        entry.Properties["ExecutionId"].Should().Be("exec-alpha");
        entry.Properties["ContinuationId"].Should().Be("continuation-alpha");
        entry.Properties["DispatchId"].Should().Be("dispatch-alpha");
        entry.Properties["ElapsedMs"].Should().Be(42L);
        entry.Message.Should().NotContain("arguments")
            .And.NotContain("result_json")
            .And.NotContain("authorization")
            .And.NotContain("secret");
    }

    [Fact]
    public void Record_WhenMetricListenerAndLoggerThrow_ShouldRemainBestEffort()
    {
        var metricCallbacks = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == WorkflowToolCallTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            metricCallbacks++;
            throw new InvalidOperationException("counter listener failed");
        });
        listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            metricCallbacks++;
            throw new InvalidOperationException("histogram listener failed");
        });
        listener.Start();

        var act = () => WorkflowToolCallTelemetry.Record(new ThrowingLogger(), Observation());

        act.Should().NotThrow();
        metricCallbacks.Should().BeGreaterThan(0);
    }

    private static WorkflowToolCallAttemptTimingObservation Observation() =>
        new()
        {
            ScopeId = "scope-alpha",
            RunId = "run-alpha",
            StepId = "step-alpha",
            CallId = "call-alpha",
            ExecutionId = "exec-alpha",
            ContinuationId = "continuation-alpha",
            Attempt = 2,
            DispatchId = "dispatch-alpha",
            Waterline = WorkflowToolCallAttemptWaterline.ProviderReturned,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero)),
            ProviderDisposition = WorkflowToolCallProviderDisposition.Succeeded,
            ExternalExecutionElapsedMs = 42,
        };

    private static MeterListener Listen(List<Measurement> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == WorkflowToolCallTelemetry.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, CopyTags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new Measurement(instrument.Name, value, CopyTags(tags))));
        listener.Start();
        return listener;
    }

    private static IReadOnlyDictionary<string, object?> CopyTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
            copy[tag.Key] = tag.Value;
        return copy;
    }

    private sealed record Measurement(
        string Instrument,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values
                    .Where(static value => !string.Equals(value.Key, "{OriginalFormat}", StringComparison.Ordinal))
                    .ToDictionary(static value => value.Key, static value => value.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add(new LogEntry(formatter(state, exception), properties));
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger failed");
    }

    private sealed record LogEntry(
        string Message,
        IReadOnlyDictionary<string, object?> Properties);
}
