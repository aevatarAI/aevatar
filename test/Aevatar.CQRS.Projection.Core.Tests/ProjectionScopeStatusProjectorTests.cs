using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionScopeStatusProjectorTests
{
    [Fact]
    public async Task ProjectAsync_CreatesStatusDocumentFromCommittedProjectionScopeState()
    {
        var dispatcher = new RecordingStatusDispatcher();
        var clock = new FixedProjectionClock(new DateTimeOffset(2026, 5, 20, 1, 2, 3, TimeSpan.Zero));
        var sut = new ProjectionScopeStatusProjector(dispatcher, clock);
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            ObservationAttached = true,
            HighestSeenVersion = 12,
            LastSuccessfulVersion = 11,
            ReceivedEnvelopeTotal = 13,
            AttemptedEnvelopeTotal = 12,
            SuccessfulMaterializationTotal = 11,
            FailedAttemptTotal = 1,
            RetryExhaustedTotal = 2,
            FailureDiagnosticDroppedTotal = 4,
        };
        state.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-1",
            RetryExhausted = true,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 5, 20, 2, 0, 0, TimeSpan.Zero)),
        });
        state.HighestSeenVersionsByActor["actor-alpha"] = 12;
        state.LastSuccessfulVersionsByActor["actor-alpha"] = 11;
        state.RecentObservedEnvelopes.Add(new ProjectionObservedEnvelopeMetadata
        {
            EventId = "source-event-12",
            TypeUrl = "type.googleapis.com/aevatar.SourceEvent",
            StateVersion = 12,
            TimestampUtc = Timestamp.FromDateTimeOffset(
                new DateTimeOffset(2026, 5, 20, 3, 4, 5, TimeSpan.Zero)),
        });

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 7, eventId: "state-event-7"));

        var document = dispatcher.Upserts.Should().ContainSingle().Which;
        document.Id.Should().Be(ProjectionScopeActorId.Build(
            new ProjectionRuntimeScopeKey(
                "root-actor",
                "channel-bot-registration",
                ProjectionRuntimeMode.DurableMaterialization)));
        document.ScopeActorId.Should().Be(document.Id);
        ((IProjectionReadModel)document).ActorId.Should().Be(document.ScopeActorId);
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("state-event-7");
        document.UpdatedAt.Should().Be(new DateTimeOffset(2026, 5, 20, 4, 5, 6, TimeSpan.Zero));
        document.Active.Should().BeTrue();
        document.ObservationAttached.Should().BeTrue();
        document.Released.Should().BeFalse();
        document.HighestSeenVersion.Should().Be(12);
        document.LastSuccessfulVersion.Should().Be(11);
        document.UnresolvedFailureCount.Should().Be(1);
        document.OldestUnresolvedFailureAtUtc.Should().Be(state.Failures[0].OccurredAtUtc);
        document.ReceivedEnvelopeTotal.Should().Be(13);
        document.AttemptedEnvelopeTotal.Should().Be(12);
        document.SuccessfulMaterializationTotal.Should().Be(11);
        document.FailedAttemptTotal.Should().Be(1);
        document.RetryExhaustedTotal.Should().Be(2);
        document.RetryExhaustedFailureCount.Should().Be(1);
        document.FailureDiagnosticDroppedTotal.Should().Be(4);
        document.SourceVersions.Should().ContainSingle().Which.VersionGap.Should().Be(1);
        document.RecentObservedEnvelopes.Should().BeEquivalentTo(state.RecentObservedEnvelopes);
    }

    [Fact]
    public async Task ProjectAsync_UpdatesOneStatusDocumentByScopeActorId()
    {
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            LastSuccessfulVersion = 21,
        };

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 2, eventId: "state-event-2"));
        state.LastSuccessfulVersion = 34;
        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 3, eventId: "state-event-3"));

        dispatcher.Upserts.Should().HaveCount(2);
        dispatcher.Upserts.Select(x => x.Id).Should().OnlyContain(id => id == dispatcher.Upserts[0].Id);
        dispatcher.Upserts[^1].StateVersion.Should().Be(3);
        dispatcher.Upserts[^1].LastSuccessfulVersion.Should().Be(34);
    }

    [Fact]
    public async Task ProjectAsync_MaterializesReleasedState()
    {
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(
                new ProjectionScopeState
                {
                    RootActorId = "root-actor",
                    ProjectionKind = "agent-registry",
                    Mode = ProjectionScopeMode.DurableMaterialization,
                    Active = true,
                    Released = true,
                    LastSuccessfulVersion = 17,
                },
                version: 4,
                eventId: "state-event-4"));

        dispatcher.Upserts.Should().ContainSingle().Which.Released.Should().BeTrue();
    }

    [Fact]
    public async Task ProjectAsync_MapsDurableFailuresToUnresolvedCountOnly()
    {
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "device-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
        };
        state.Failures.Add(new ProjectionScopeFailure { FailureId = "failure-1", Reason = "boom" });
        state.Failures.Add(new ProjectionScopeFailure { FailureId = "failure-2", Reason = "boom again" });

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 5, eventId: "state-event-5"));

        var document = dispatcher.Upserts.Should().ContainSingle().Which;
        document.UnresolvedFailureCount.Should().Be(2);
        typeof(ProjectionScopeStatusDocument).GetProperty("Failures").Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_IgnoresNonProjectionScopeState()
    {
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var envelope = new EventEnvelope
        {
            Id = "outer-1",
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "state-event-1",
                    Version = 1,
                    EventData = Any.Pack(new StringValue { Value = "payload" }),
                },
                StateRoot = Any.Pack(new StringValue { Value = "not-scope-state" }),
            }),
        };

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            envelope);

        dispatcher.Upserts.Should().BeEmpty();
    }

    private static EventEnvelope CreateCommittedEnvelope(
        ProjectionScopeState state,
        long version,
        string eventId)
    {
        var timestamp = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 5, 20, 4, 5, 6, TimeSpan.Zero));
        return new EventEnvelope
        {
            Id = $"outer-{version}",
            Timestamp = timestamp,
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
                        state.RootActorId,
                        state.ProjectionKind,
                        ProjectionScopeModeMapper.ToRuntime(state.Mode),
                        state.SessionId)),
                    EventId = eventId,
                    Version = version,
                    Timestamp = timestamp,
                    EventData = Any.Pack(new ProjectionScopeWatermarkAdvancedEvent
                    {
                        LastSuccessfulVersion = state.LastSuccessfulVersion,
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class FixedProjectionClock : IProjectionClock
    {
        public FixedProjectionClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingStatusDispatcher
        : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
    {
        public List<ProjectionScopeStatusDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ProjectionScopeStatusDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            _ = id;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }
}
