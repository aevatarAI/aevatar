using System.Diagnostics.Metrics;
using Aevatar.CQRS.Projection.Core.Observability;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionProcessingTelemetryTests
{
    [Fact]
    public void StateApplier_KeepsOlderFailureUnresolvedAfterNewerVersionSucceeds()
    {
        var state = new ProjectionScopeState();
        state = ProjectionScopeStateApplier.ApplyEnvelopeAttempted(state, Attempt("actor-alpha", 1));
        state = ProjectionScopeStateApplier.ApplyDispatchFailed(state, Failure("failure-1", 1));
        state = ProjectionScopeStateApplier.ApplyEnvelopeAttempted(state, Attempt("actor-alpha", 2));
        state = ProjectionScopeStateApplier.ApplyWatermarkAdvanced(state, Success("actor-alpha", 2));

        state.HighestSeenVersionsByActor["actor-alpha"].Should().Be(2);
        state.LastSuccessfulVersionsByActor["actor-alpha"].Should().Be(2);
        state.SuccessfulMaterializationTotal.Should().Be(1);
        state.FailedAttemptTotal.Should().Be(1);
        state.Failures.Should().ContainSingle().Which.FailureId.Should().Be("failure-1");
        state.FailureSummary.UnresolvedFailureCount.Should().Be(1);
        state.FailureSummary.RetryExhaustedFailureCount.Should().Be(0);
    }

    [Fact]
    public async Task FailureTracker_OnSixtyFifthFailure_DropsOnlyDiagnosticAndPublishesDedicatedAlert()
    {
        var state = new ProjectionScopeState();
        var alerts = new RecordingAlertSink();
        var tracker = new ProjectionScopeFailureTracker(
            evt =>
            {
                state = ProjectionScopeStateApplier.ApplyDispatchFailed(
                    state,
                    (ProjectionScopeDispatchFailedEvent)evt);
                return Task.CompletedTask;
            },
            () => alerts,
            () => new ProjectionRuntimeScopeKey(
                "actor-alpha",
                "projector-alpha",
                ProjectionRuntimeMode.DurableMaterialization),
            () => state.Failures.Count,
            () => state.RetainedFailureDiagnostics.Count,
            () => state.RetainedFailureDiagnostics.FirstOrDefault(),
            () => state.Failures.FirstOrDefault()?.OccurredAtUtc?.ToDateTimeOffset(),
            () => state.FailureDiagnosticDroppedTotal);

        for (var version = 1; version <= 65; version++)
        {
            await tracker.RecordAsync(
                "projection-execution",
                $"event-{version}",
                "type.googleapis.com/aevatar.TestEvent",
                version,
                "boom",
                new EventEnvelope
                {
                    Id = $"envelope-{version}",
                    Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-alpha"),
                },
                NullLogger.Instance);
        }

        state.Failures.Should().HaveCount(65, "the operator repair backlog is durable and untrimmed");
        state.FailureSummary.UnresolvedFailureCount.Should().Be(65);
        state.RetainedFailureDiagnostics.Should().HaveCount(64);
        state.RetainedFailureDiagnostics[0].EventId.Should().Be("event-2");
        state.Failures.Should().OnlyContain(failure => failure.SourceActorId == "actor-alpha");
        state.RetainedFailureDiagnostics.Should()
            .OnlyContain(diagnostic => diagnostic.SourceActorId == "actor-alpha");
        state.FailureDiagnosticDroppedTotal.Should().Be(1);
        var dropped = alerts.Alerts.Should()
            .ContainSingle(alert => alert.Kind == ProjectionFailureAlertKind.DiagnosticRetentionDropped)
            .Subject;
        dropped.DroppedCount.Should().Be(1);
        dropped.DroppedFailureIds.Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
        dropped.DiagnosticDroppedTotal.Should().Be(1);
        dropped.UnresolvedFailureCount.Should().Be(65);
    }

    [Fact]
    public void FailedReplay_IncrementsFailureTotalAndExhaustsOnlyOnce()
    {
        var state = ProjectionScopeStateApplier.ApplyDispatchFailed(
            new ProjectionScopeState(),
            Failure("failure-alpha", 1));

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            state = ProjectionScopeStateApplier.ApplyFailureReplayed(
                state,
                new ProjectionScopeFailureReplayedEvent
                {
                    FailureId = "failure-alpha",
                    Succeeded = false,
                    Reason = "still failing",
                    OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                });
        }

        state.FailedAttemptTotal.Should().Be(5);
        state.RetryExhaustedTotal.Should().Be(1);
        state.Failures.Should().ContainSingle().Which.RetryExhausted.Should().BeTrue();
        state.FailureSummary.UnresolvedFailureCount.Should().Be(1);
        state.FailureSummary.RetryExhaustedFailureCount.Should().Be(1);
    }

    [Fact]
    public void SuccessfulReplay_RecomputesSummaryAfterOldestFailureIsRemoved()
    {
        var oldestAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 17, 14, 47, 56, TimeSpan.Zero));
        var newerAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 17, 14, 52, 56, TimeSpan.Zero));
        var oldestFailure = Failure("failure-oldest", 1);
        oldestFailure.OccurredAtUtc = oldestAt;
        var newerFailure = Failure("failure-newer", 2);
        newerFailure.OccurredAtUtc = newerAt;
        var state = ProjectionScopeStateApplier.ApplyDispatchFailed(
            new ProjectionScopeState(),
            oldestFailure);
        state = ProjectionScopeStateApplier.ApplyDispatchFailed(state, newerFailure);
        for (var attempt = 0; attempt < ProjectionFailureRetentionPolicy.DefaultMaxReplayAttempts; attempt++)
        {
            state = ProjectionScopeStateApplier.ApplyFailureReplayed(
                state,
                new ProjectionScopeFailureReplayedEvent
                {
                    FailureId = "failure-newer",
                    Succeeded = false,
                    Reason = "still failing",
                    OccurredAtUtc = newerAt,
                });
        }

        state = ProjectionScopeStateApplier.ApplyFailureReplayed(
            state,
            new ProjectionScopeFailureReplayedEvent
            {
                FailureId = "failure-oldest",
                Succeeded = true,
                OccurredAtUtc = newerAt,
            });

        state.Failures.Should().ContainSingle().Which.FailureId.Should().Be("failure-newer");
        state.FailureSummary.UnresolvedFailureCount.Should().Be(1);
        state.FailureSummary.RetryExhaustedFailureCount.Should().Be(1);
        state.FailureSummary.OldestUnresolvedFailureAtUtc.Should().Be(newerAt);
    }

    [Fact]
    public void Metrics_UseOnlyStableLowCardinalityLabels()
    {
        var measurements = new List<(string Instrument, string[] TagKeys)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProjectionProcessingMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        listener.Start();

        ProjectionProcessingMetrics.RecordAttempted("projector-alpha", "event-kind-alpha");
        ProjectionProcessingMetrics.RecordSucceeded(
            "projector-alpha",
            "event-kind-alpha",
            TimeSpan.FromMilliseconds(4));
        ProjectionProcessingMetrics.RecordFailed(
            "projector-alpha",
            "event-kind-alpha",
            unresolvedCount: 1,
            oldestOccurredAt: DateTimeOffset.UtcNow.AddSeconds(-2),
            addsUnresolvedFailure: true);

        measurements.Should().NotBeEmpty();
        measurements.SelectMany(measurement => measurement.TagKeys)
            .Should().OnlyContain(key => key == ProjectionProcessingMetrics.ProjectionKindTag ||
                                         key == ProjectionProcessingMetrics.EventKindTag);
        measurements.SelectMany(measurement => measurement.TagKeys)
            .Should().NotContain(key => key.Contains("actor", StringComparison.OrdinalIgnoreCase) ||
                                        key.Contains("session", StringComparison.OrdinalIgnoreCase) ||
                                        key.Contains("command", StringComparison.OrdinalIgnoreCase) ||
                                        key.Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MaterializerMetrics_RecordDurationAndTotal_WithExactTagAllowlist()
    {
        const string projectionKind = "materializer-tag-allowlist-projection";
        var measurements = new List<MaterializerMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProjectionProcessingMetrics.MeterName &&
                (instrument.Name == ProjectionProcessingMetrics.MaterializerDurationMetricName ||
                 instrument.Name == ProjectionProcessingMetrics.MaterializerTotalMetricName))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            CaptureMaterializerMeasurement(measurements, projectionKind, instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            CaptureMaterializerMeasurement(measurements, projectionKind, instrument.Name, value, tags));
        listener.Start();

        ProjectionProcessingMetrics.RecordMaterializerTerminal(
            projectionKind,
            "MaterializerAlpha",
            ProjectionProcessingMetrics.ResultCompleted,
            TimeSpan.FromMilliseconds(7));
        ProjectionProcessingMetrics.RecordMaterializerTerminal(
            projectionKind,
            "MaterializerAlpha",
            ProjectionProcessingMetrics.ResultFailed,
            TimeSpan.FromMilliseconds(11));
        ProjectionProcessingMetrics.RecordMaterializerTerminal(
            projectionKind,
            "MaterializerAlpha",
            ProjectionProcessingMetrics.ResultCancelled,
            TimeSpan.FromMilliseconds(13));

        measurements.Should().HaveCount(6);
        measurements.Count(measurement =>
                measurement.Instrument == ProjectionProcessingMetrics.MaterializerDurationMetricName)
            .Should().Be(3);
        measurements.Count(measurement =>
                measurement.Instrument == ProjectionProcessingMetrics.MaterializerTotalMetricName)
            .Should().Be(3);
        measurements.Where(measurement =>
                measurement.Instrument == ProjectionProcessingMetrics.MaterializerTotalMetricName)
            .Should().OnlyContain(measurement => measurement.Value == 1);
        measurements.Select(measurement => measurement.Tags[ProjectionProcessingMetrics.ResultTag])
            .Should().BeEquivalentTo(new[]
            {
                ProjectionProcessingMetrics.ResultCompleted,
                ProjectionProcessingMetrics.ResultCompleted,
                ProjectionProcessingMetrics.ResultFailed,
                ProjectionProcessingMetrics.ResultFailed,
                ProjectionProcessingMetrics.ResultCancelled,
                ProjectionProcessingMetrics.ResultCancelled,
            });
        var allowedTagKeys = new[]
        {
            ProjectionProcessingMetrics.ProjectionKindTag,
            ProjectionProcessingMetrics.MaterializerKindTag,
            ProjectionProcessingMetrics.ResultTag,
        };
        measurements.Should().OnlyContain(measurement =>
            measurement.Tags.Keys.SequenceEqual(allowedTagKeys));
        measurements.SelectMany(measurement => measurement.Tags.Keys)
            .Should().NotContain(key =>
                key.Contains("state", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("scope", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("owner", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("actor", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("node", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("edge", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("id", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ObservedMaterializers_WithSameContext_EmitDistinctConcreteMaterializerKinds()
    {
        const string projectionKind = "materializer-wrapper-kind-projection";
        var measurements = new List<MaterializerMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProjectionProcessingMetrics.MeterName &&
                (instrument.Name == ProjectionProcessingMetrics.MaterializerDurationMetricName ||
                 instrument.Name == ProjectionProcessingMetrics.MaterializerTotalMetricName))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            CaptureMaterializerMeasurement(measurements, projectionKind, instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            CaptureMaterializerMeasurement(measurements, projectionKind, instrument.Name, value, tags));
        listener.Start();

        var context = new MaterializerTelemetryContext
        {
            ProjectionKind = projectionKind,
        };
        var envelope = new EventEnvelope { Id = "materializer-wrapper-kind-event" };
        var alphaInner = new AlphaTelemetryMaterializer();
        var betaInner = new BetaTelemetryMaterializer();
        var alpha = new ObservedProjectionMaterializer<MaterializerTelemetryContext, AlphaTelemetryMaterializer>(
            alphaInner);
        var beta = new ObservedProjectionMaterializer<MaterializerTelemetryContext, BetaTelemetryMaterializer>(
            betaInner);

        await alpha.ProjectAsync(context, envelope);
        await beta.ProjectAsync(context, envelope);

        alphaInner.ProjectCount.Should().Be(1);
        betaInner.ProjectCount.Should().Be(1);
        measurements.Should().HaveCount(4);
        var expectedKinds = new[]
        {
            nameof(AlphaTelemetryMaterializer),
            nameof(BetaTelemetryMaterializer),
        };
        measurements
            .Select(measurement => measurement.Tags[ProjectionProcessingMetrics.MaterializerKindTag])
            .Distinct()
            .Should()
            .BeEquivalentTo(expectedKinds);
        foreach (var materializerKind in expectedKinds)
        {
            var materializerMeasurements = measurements
                .Where(measurement => Equals(
                    measurement.Tags[ProjectionProcessingMetrics.MaterializerKindTag],
                    materializerKind))
                .ToArray();
            materializerMeasurements.Should().HaveCount(2);
            materializerMeasurements.Select(measurement => measurement.Instrument).Should().BeEquivalentTo(
                ProjectionProcessingMetrics.MaterializerDurationMetricName,
                ProjectionProcessingMetrics.MaterializerTotalMetricName);
            materializerMeasurements.Should().OnlyContain(measurement =>
                Equals(
                    measurement.Tags[ProjectionProcessingMetrics.ResultTag],
                    ProjectionProcessingMetrics.ResultCompleted));
        }
    }

    [Fact]
    public void MaterializerMetrics_ShouldNotPropagateListenerFailure()
    {
        const string projectionKind = "materializer-throwing-listener-projection";
        var counterCallbacks = 0;
        var histogramCallbacks = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProjectionProcessingMetrics.MeterName &&
                (instrument.Name == ProjectionProcessingMetrics.MaterializerDurationMetricName ||
                 instrument.Name == ProjectionProcessingMetrics.MaterializerTotalMetricName))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!IsProjection(tags, projectionKind))
                return;

            Interlocked.Increment(ref counterCallbacks);
            throw new InvalidOperationException("counter listener boom");
        });
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (!IsProjection(tags, projectionKind))
                return;

            Interlocked.Increment(ref histogramCallbacks);
            throw new InvalidOperationException("histogram listener boom");
        });
        listener.Start();

        Action act = () => ProjectionProcessingMetrics.RecordMaterializerTerminal(
            projectionKind,
            "MaterializerAlpha",
            ProjectionProcessingMetrics.ResultCompleted,
            TimeSpan.FromMilliseconds(3));

        act.Should().NotThrow();
        counterCallbacks.Should().Be(1);
        histogramCallbacks.Should().Be(1);
    }

    private static void CaptureMaterializerMeasurement<T>(
        ICollection<MaterializerMeasurement> measurements,
        string projectionKind,
        string instrument,
        T value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct, IConvertible
    {
        var tagValues = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);
        if (!Equals(tagValues.GetValueOrDefault(ProjectionProcessingMetrics.ProjectionKindTag), projectionKind))
            return;

        measurements.Add(new MaterializerMeasurement(instrument, Convert.ToDouble(value), tagValues));
    }

    private static bool IsProjection(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string projectionKind) =>
        tags.ToArray().Any(tag =>
            tag.Key == ProjectionProcessingMetrics.ProjectionKindTag &&
            Equals(tag.Value, projectionKind));

    private sealed record MaterializerMeasurement(
        string Instrument,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);

    private sealed class MaterializerTelemetryContext : IProjectionMaterializationContext
    {
        public string RootActorId { get; init; } = "actor-materializer-telemetry";

        public string ProjectionKind { get; init; } = "materializer-telemetry";
    }

    private sealed class AlphaTelemetryMaterializer : IProjectionMaterializer<MaterializerTelemetryContext>
    {
        public int ProjectCount { get; private set; }

        public ValueTask ProjectAsync(
            MaterializerTelemetryContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            ProjectCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BetaTelemetryMaterializer : IProjectionMaterializer<MaterializerTelemetryContext>
    {
        public int ProjectCount { get; private set; }

        public ValueTask ProjectAsync(
            MaterializerTelemetryContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            ProjectCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static ProjectionScopeEnvelopeAttemptedEvent Attempt(string sourceActorId, long version) =>
        new()
        {
            SourceActorId = sourceActorId,
            HighestSeenVersion = version,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private static ProjectionScopeDispatchFailedEvent Failure(string failureId, long version) =>
        new()
        {
            FailureId = failureId,
            EventId = $"event-{version}",
            EventType = "type.googleapis.com/aevatar.TestEvent",
            SourceVersion = version,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private static ProjectionScopeWatermarkAdvancedEvent Success(string sourceActorId, long version) =>
        new()
        {
            SourceActorId = sourceActorId,
            LastSuccessfulVersion = version,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };

    private sealed class RecordingAlertSink : IProjectionFailureAlertSink
    {
        public List<ProjectionFailureAlert> Alerts { get; } = [];

        public Task PublishAsync(ProjectionFailureAlert alert, CancellationToken ct = default)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }
}
