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
