using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeCommittedStateRedactionHookTests
{
    private static readonly System.Type ProjectionActorType =
        typeof(ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>);

    [Fact]
    public async Task BeforePublishAsync_ShouldPublishTransportSafeFailureSummaryWithoutMutatingAuthority()
    {
        var sourceEnvelope = BuildEnvelope("source-envelope");
        var sourceFailureEvent = BuildFailureEvent(sourceEnvelope);
        var sourceState = BuildState(sourceEnvelope);
        var published = BuildPublished(sourceState, sourceFailureEvent);
        var originalEventId = published.StateEvent.EventId;
        var originalVersion = published.StateEvent.Version;
        var expectedFailureEvent = sourceFailureEvent.Clone();
        expectedFailureEvent.Envelope = null;
        expectedFailureEvent.Reason = string.Empty;

        await InvokeAsync(published, ProjectionActorType);

        published.StateEvent.EventId.Should().Be(originalEventId);
        published.StateEvent.Version.Should().Be(originalVersion);
        var outboundState = published.StateRoot.Unpack<ProjectionScopeState>();
        outboundState.FailureSummary.UnresolvedFailureCount.Should().Be(2);
        outboundState.FailureSummary.RetryExhaustedFailureCount.Should().Be(1);
        outboundState.FailureSummary.OldestUnresolvedFailureAtUtc.Should()
            .Be(sourceState.Failures[0].OccurredAtUtc);
        outboundState.Failures.Should().HaveCount(2);
        outboundState.Failures.Should().OnlyContain(failure =>
            failure.Envelope == null &&
            string.IsNullOrEmpty(failure.FailureId) &&
            string.IsNullOrEmpty(failure.Reason));
        published.StateEvent.EventData.Unpack<ProjectionScopeDispatchFailedEvent>()
            .Should().Be(expectedFailureEvent);
        sourceState.Failures[0].Envelope.Should().BeSameAs(sourceEnvelope);
        sourceState.Failures[0].Reason.Should().Be("materialization failed");
        sourceFailureEvent.Envelope.Should().BeSameAs(sourceEnvelope);
        sourceFailureEvent.Reason.Should().Be("materialization failed");
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldStripCompatibilityFailurePayloadsAndUncontrolledReasons()
    {
        var envelope = BuildEnvelope("bounded-envelope");
        var state = BuildState(envelope);
        for (var index = state.Failures.Count; index < 70; index++)
        {
            state.Failures.Add(new ProjectionScopeFailure
            {
                FailureId = $"failure-{index}",
                Reason = new string('x', 64 * 1024),
                Envelope = envelope,
                RetryExhausted = index % 2 == 0,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 8, 17, 15, 7, 6, TimeSpan.Zero).AddMinutes(index)),
            });
        }
        var failureEvent = BuildFailureEvent(envelope);
        failureEvent.Reason = new string('y', 2 * 1024 * 1024);
        var published = BuildPublished(state, failureEvent);

        await InvokeAsync(published, ProjectionActorType);

        var outbound = published.StateRoot.Unpack<ProjectionScopeState>();
        outbound.FailureSummary.UnresolvedFailureCount.Should().Be(70);
        outbound.FailureSummary.RetryExhaustedFailureCount.Should().Be(35);
        outbound.Failures.Should().HaveCount(70);
        outbound.CalculateSize().Should().BeLessThan(16 * 1024);
        var outboundEvent = published.StateEvent.EventData.Unpack<ProjectionScopeDispatchFailedEvent>();
        outboundEvent.Envelope.Should().BeNull();
        outboundEvent.Reason.Should().BeEmpty();
        published.CalculateSize().Should().BeLessThan(32 * 1024);

        state.Failures.Should().HaveCount(70);
        state.Failures.Should().Contain(failure => failure.Reason.Length == 64 * 1024);
        failureEvent.Reason.Should().HaveLength(2 * 1024 * 1024);
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldRecomputeStaleFailureSummary()
    {
        var envelope = BuildEnvelope("stale-summary-envelope");
        var state = BuildState(envelope);
        state.FailureSummary = new ProjectionScopeFailureSummary
        {
            UnresolvedFailureCount = 99,
            RetryExhaustedFailureCount = 98,
            OldestUnresolvedFailureAtUtc = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        };
        var published = BuildPublished(state, BuildFailureEvent(envelope));

        await InvokeAsync(published, ProjectionActorType);

        var summary = published.StateRoot.Unpack<ProjectionScopeState>().FailureSummary;
        summary.UnresolvedFailureCount.Should().Be(2);
        summary.RetryExhaustedFailureCount.Should().Be(1);
        summary.OldestUnresolvedFailureAtUtc.Should().Be(state.Failures[0].OccurredAtUtc);
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldBeIdempotent()
    {
        var envelope = BuildEnvelope("idempotent-envelope");
        var published = BuildPublished(BuildState(envelope), BuildFailureEvent(envelope));

        await InvokeAsync(published, ProjectionActorType);
        var first = published.ToByteArray();
        await InvokeAsync(published, ProjectionActorType);

        published.ToByteArray().Should().Equal(first);
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldIgnoreNonProjectionActorsAndOtherStateTypes()
    {
        var envelope = BuildEnvelope("ignored-envelope");
        var nonProjectionPublication = BuildPublished(BuildState(envelope), BuildFailureEvent(envelope));
        var nonProjectionBytes = nonProjectionPublication.ToByteArray();
        var otherStatePublication = BuildPublished(BuildState(envelope), BuildFailureEvent(envelope));
        otherStatePublication.StateRoot = Any.Pack(new StringValue { Value = "not-projection-state" });
        var otherStateBytes = otherStatePublication.ToByteArray();

        await InvokeAsync(nonProjectionPublication, typeof(object));
        await InvokeAsync(otherStatePublication, ProjectionActorType);

        nonProjectionPublication.ToByteArray().Should().Equal(nonProjectionBytes);
        otherStatePublication.ToByteArray().Should().Equal(otherStateBytes);
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldLeaveOtherEventPayloadsUnchanged()
    {
        var envelope = BuildEnvelope("state-only-envelope");
        var published = BuildPublished(BuildState(envelope), BuildFailureEvent(envelope));
        published.StateEvent.EventData = Any.Pack(new StringValue { Value = "other-event" });
        var originalEventData = published.StateEvent.EventData;

        await InvokeAsync(published, ProjectionActorType);

        published.StateRoot.Unpack<ProjectionScopeState>().Failures[0].Envelope.Should().BeNull();
        published.StateEvent.EventData.Should().BeSameAs(originalEventData);
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldRemoveOutboundReplayFailureReason()
    {
        var envelope = BuildEnvelope("replay-envelope");
        var published = BuildPublished(BuildState(envelope), BuildFailureEvent(envelope));
        var replayed = new ProjectionScopeFailureReplayedEvent
        {
            FailureId = "failure-1",
            Reason = new string('z', 2 * 1024 * 1024),
        };
        published.StateEvent.EventData = Any.Pack(replayed);

        await InvokeAsync(published, ProjectionActorType);

        published.StateEvent.EventData.Unpack<ProjectionScopeFailureReplayedEvent>()
            .Reason.Should().BeEmpty();
        replayed.Reason.Should().HaveLength(2 * 1024 * 1024);
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldRedactInFlightRecoveryEnvelopeWithoutMutatingAuthority()
    {
        var envelope = BuildEnvelope("in-flight-envelope");
        var state = BuildState(envelope);
        state.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "workflow.run:test",
                StateVersion = 1474,
                EventId = "source-event-1474",
            },
            Envelope = envelope,
        };
        var staged = new ProjectionScopeObservationStagedEvent
        {
            Observation = state.InFlightObservation.Clone(),
        };
        var published = BuildPublished(state, BuildFailureEvent(envelope));
        published.StateEvent.EventData = Any.Pack(staged);

        await InvokeAsync(published, ProjectionActorType);

        var outboundState = published.StateRoot.Unpack<ProjectionScopeState>();
        outboundState.InFlightObservation.Source.Should().Be(state.InFlightObservation.Source);
        outboundState.InFlightObservation.Envelope.Should().BeNull();
        var outboundEvent = published.StateEvent.EventData
            .Unpack<ProjectionScopeObservationStagedEvent>();
        outboundEvent.Observation.Source.Should().Be(staged.Observation.Source);
        outboundEvent.Observation.Envelope.Should().BeNull();
        state.InFlightObservation.Envelope.Should().BeSameAs(envelope);
        staged.Observation.Envelope.Should().Be(envelope);
    }

    private static Task InvokeAsync(CommittedStateEventPublished published, System.Type actorType) =>
        new ProjectionScopeCommittedStateRedactionHook().BeforePublishAsync(
            new CommittedStatePublicationContext
            {
                ActorId = "projection.durable.scope:test",
                ActorType = actorType,
                Published = published,
            },
            CancellationToken.None);

    private static CommittedStateEventPublished BuildPublished(
        ProjectionScopeState state,
        ProjectionScopeDispatchFailedEvent failure) =>
        new()
        {
            StateEvent = new StateEvent
            {
                AgentId = "projection.durable.scope:test",
                EventId = "committed-event-42",
                EventType = ProjectionScopeDispatchFailedEvent.Descriptor.FullName,
                EventData = Any.Pack(failure),
                Version = 42,
            },
            StateRoot = Any.Pack(state),
        };

    private static ProjectionScopeState BuildState(EventEnvelope envelope)
    {
        var state = new ProjectionScopeState
        {
            RootActorId = "workflow.run:test",
            ProjectionKind = "workflow-execution-materialization",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            HighestSeenVersion = 1474,
            LastSuccessfulVersion = 1465,
            FailedAttemptTotal = 8,
            RetryExhaustedTotal = 1,
            ActivationGeneration = 3,
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 17, 15, 7, 6, TimeSpan.Zero)),
        };
        state.LastSuccessfulVersionsByActor.Add("workflow.run:test", 1465);
        state.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-1",
            Stage = "projection-execution",
            EventId = "source-event-1474",
            EventType = "type.googleapis.com/aevatar.workflow.WorkflowCompletedEvent",
            SourceVersion = 1474,
            Reason = "materialization failed",
            Envelope = envelope,
            Attempts = 8,
            RetryExhausted = true,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 8, 17, 14, 47, 56, TimeSpan.Zero)),
            SourceActorId = "workflow.run:test",
        });
        state.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-without-envelope",
            EventId = "source-event-1465",
            SourceVersion = 1465,
        });
        return state;
    }

    private static ProjectionScopeDispatchFailedEvent BuildFailureEvent(EventEnvelope envelope) =>
        new()
        {
            FailureId = "failure-1",
            Stage = "projection-execution",
            EventId = "source-event-1474",
            EventType = "type.googleapis.com/aevatar.workflow.WorkflowCompletedEvent",
            SourceVersion = 1474,
            Reason = "materialization failed",
            Envelope = envelope,
            SourceActorId = "workflow.run:test",
        };

    private static EventEnvelope BuildEnvelope(string id) =>
        new()
        {
            Id = id,
            Payload = Any.Pack(new StringValue { Value = "repair-payload" }),
        };
}
