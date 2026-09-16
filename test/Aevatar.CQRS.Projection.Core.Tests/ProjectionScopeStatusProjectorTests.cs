using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using FluentAssertions;
using Google.Protobuf;
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
        state.LastSuccessfulSourceCoordinatesByActor["actor-alpha"] = new ProjectionSourceCoordinate
        {
            ActorId = "actor-alpha",
            StateVersion = 11,
            EventId = "source-event-11",
        };
        state.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "actor-alpha",
                StateVersion = 12,
                EventId = "source-event-12",
            },
            Envelope = new EventEnvelope
            {
                Payload = Any.Pack(new StringValue { Value = "must-not-project" }),
            },
        };
        state.ActiveMaterializationRoute = new ProjectionMaterializationRouteFingerprint
        {
            ContractId = "workflow-run-graph",
            ContractVersion = 2,
            PhysicalNamespace = "workflow-execution-graph-v2",
            RouteEpoch = 4,
        };
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
        document.LastSuccessfulSourceCoordinates.Should().BeEquivalentTo(
            state.LastSuccessfulSourceCoordinatesByActor.Values);
        document.InFlightSource.Should().Be(state.InFlightObservation.Source);
        document.ActiveMaterializationRoute.Should().Be(state.ActiveMaterializationRoute);
        document.StatusRoute.Should().BeNull("a pre-route source has no status route to carry");
        ((IProjectionRouteFencedReadModel)document).RouteEpoch.Should().Be(0);
        typeof(ProjectionScopeStatusDocument).GetProperty("InFlightObservation").Should().BeNull();
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
    public async Task ProjectAsync_ShouldPreferTypedFailureSummaryOverCompatibilityFailures()
    {
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var oldest = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 17, 14, 47, 56, TimeSpan.Zero));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "workflow-execution-materialization",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            FailureSummary = new ProjectionScopeFailureSummary
            {
                UnresolvedFailureCount = 73,
                RetryExhaustedFailureCount = 19,
                OldestUnresolvedFailureAtUtc = oldest,
            },
        };
        state.Failures.Add(new ProjectionScopeFailure
        {
            RetryExhausted = false,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(oldest.ToDateTimeOffset().AddDays(1)),
        });

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 6, eventId: "state-event-6"));

        var document = dispatcher.Upserts.Should().ContainSingle().Which;
        document.UnresolvedFailureCount.Should().Be(73);
        document.RetryExhaustedFailureCount.Should().Be(19);
        document.OldestUnresolvedFailureAtUtc.Should().Be(oldest);
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

    // ── the gate: ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite ──────────────

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    public async Task ProjectAsync_ShouldNotWrite_WhenSourceCommittedTerminalRouteInWritingPhase(
        ProjectionScopeStatusRoutePhase phase)
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
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, phase),
        };

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 8, eventId: "state-event-8"));

        dispatcher.Upserts.Should().BeEmpty(
            "once the terminal route is blocked or active (or phase-less, i.e. active by definition) the legacy shadow may still be draining but must not write");
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotWrite_WhenTerminalRouteIsStillDrainingBeforeLegacyRelease()
    {
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2),
        };
        state.StatusRoute.LegacyRouteReleased = false;

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 9, eventId: "state-event-9"));

        dispatcher.Upserts.Should().BeEmpty("the gate is the committed route itself, not the legacy release flag");
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotWrite_WhenTerminalRouteIsNewerThanThisReader()
    {
        // The gate is the route contract: a terminal route of any contract version in a writing
        // phase selects the terminal, whether or not this binary could serve it.
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2),
        };
        state.StatusRoute.ContractVersion = ProjectionScopeStatusGAgent.ContractVersion + 1;

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 9, eventId: "state-event-9"));

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldWrite_WhenTerminalRouteIsWarming()
    {
        // Phase 1 of the cutover: the terminal observes and reports; the legacy shadow is still
        // the writer and its document carries the warming route (the fence epoch of the cutover).
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            LastSuccessfulVersion = 21,
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
                routeEpoch: 2,
                ProjectionScopeStatusRoutePhase.Warming),
        };
        state.StatusRoute.WarmStartedVersion = 10;

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 10, eventId: "state-event-10"));

        var document = dispatcher.Upserts.Should().ContainSingle().Which;
        document.StateVersion.Should().Be(10);
        document.StatusRoute.Should().Be(state.StatusRoute);
        document.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        ((IProjectionRouteFencedReadModel)document).RouteEpoch.Should().Be(2);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    public async Task ProjectAsync_ShouldWrite_WhenLegacyRouteIsInWritingPhase(ProjectionScopeStatusRoutePhase phase)
    {
        // Durable rollback completed (or a phase-less legacy route from the previous binary):
        // the legacy shadow is the writer again.
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            LastSuccessfulVersion = 21,
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 3, phase),
        };

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 11, eventId: "state-event-11"));

        var document = dispatcher.Upserts.Should().ContainSingle().Which;
        document.StateVersion.Should().Be(11);
        document.StatusRoute.Should().Be(state.StatusRoute);
        document.StatusRoute.ContractId.Should().Be(ProjectionScopeStatusRoutePolicy.LegacyContractId);
        ((IProjectionRouteFencedReadModel)document).RouteEpoch.Should().Be(3);
    }

    [Fact]
    public async Task ProjectAsync_ShouldNotWrite_WhenLegacyRouteIsWarming()
    {
        // Rollback in flight: the legacy shadow is warming (it reports caught-up to the source
        // from its own scope actor); the terminal remains the writer until the source blocks.
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            LastSuccessfulVersion = 21,
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(
                routeEpoch: 3,
                ProjectionScopeStatusRoutePhase.Warming),
        };

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 12, eventId: "state-event-12"));

        dispatcher.Upserts.Should().BeEmpty("during a rollback warming the terminal is still the writer");
    }

    [Fact]
    public async Task ProjectAsync_AcrossRollbackPhases_ShouldWriteOnlyOnceTheLegacyRouteBlocks()
    {
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var context = new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" };
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
        };

        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        await sut.ProjectAsync(context, CreateCommittedEnvelope(state, version: 20, eventId: "state-event-20"));
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 3, ProjectionScopeStatusRoutePhase.Warming);
        await sut.ProjectAsync(context, CreateCommittedEnvelope(state, version: 21, eventId: "state-event-21"));
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 3, ProjectionScopeStatusRoutePhase.Blocked);
        await sut.ProjectAsync(context, CreateCommittedEnvelope(state, version: 22, eventId: "state-event-22"));
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 3, ProjectionScopeStatusRoutePhase.Active);
        await sut.ProjectAsync(context, CreateCommittedEnvelope(state, version: 23, eventId: "state-event-23"));

        dispatcher.Upserts.Select(static document => document.StateVersion).Should().Equal(22, 23);
        dispatcher.Upserts.Select(static document => document.StatusRoute.Phase).Should().Equal(
            ProjectionScopeStatusRoutePhase.Blocked,
            ProjectionScopeStatusRoutePhase.Active);
    }

    public enum NonTerminalRouteShape
    {
        NoRoute,
        ZeroEpoch,
        MissingContractId,
        ZeroContractVersion,
    }

    [Theory]
    [InlineData(NonTerminalRouteShape.NoRoute)]
    [InlineData(NonTerminalRouteShape.ZeroEpoch)]
    [InlineData(NonTerminalRouteShape.MissingContractId)]
    [InlineData(NonTerminalRouteShape.ZeroContractVersion)]
    public async Task ProjectAsync_ShouldWrite_WhenStatusRouteIsNotATerminalRoute(NonTerminalRouteShape shape)
    {
        var dispatcher = new RecordingStatusDispatcher();
        var sut = new ProjectionScopeStatusProjector(dispatcher, new FixedProjectionClock(DateTimeOffset.UtcNow));
        var state = new ProjectionScopeState
        {
            RootActorId = "root-actor",
            ProjectionKind = "channel-bot-registration",
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            LastSuccessfulVersion = 5,
        };
        state.StatusRoute = shape switch
        {
            NonTerminalRouteShape.NoRoute => null,
            NonTerminalRouteShape.ZeroEpoch => ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 0),
            NonTerminalRouteShape.MissingContractId => new ProjectionScopeStatusRoute
            {
                ContractVersion = ProjectionScopeStatusGAgent.ContractVersion,
                RouteEpoch = 1,
            },
            NonTerminalRouteShape.ZeroContractVersion => new ProjectionScopeStatusRoute
            {
                ContractId = ProjectionScopeStatusGAgent.ContractId,
                RouteEpoch = 1,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            CreateCommittedEnvelope(state, version: 3, eventId: "state-event-3"));

        var document = dispatcher.Upserts.Should().ContainSingle().Which;
        document.StateVersion.Should().Be(3);
        // The document carries whatever route the source committed (even a malformed one) and
        // fences on its epoch; the mapping is the shared one, so the same state maps to the
        // same bytes for either writer.
        document.StatusRoute.Should().Be(state.StatusRoute);
        ((IProjectionRouteFencedReadModel)document).RouteEpoch.Should().Be(state.StatusRoute?.RouteEpoch ?? 0);
        var envelope = CreateCommittedEnvelope(state, version: 3, eventId: "state-event-3");
        CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var unpackedState)
            .Should().BeTrue();
        var mapped = ProjectionScopeStatusDocumentMapper.Map(
            unpackedState!,
            stateEvent!,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, DateTimeOffset.UtcNow));
        document.ToByteString().Equals(mapped.ToByteString()).Should().BeTrue();
        ProjectionWriteResultEvaluator.Evaluate(document, mapped).Disposition
            .Should().Be(ProjectionWriteDisposition.Duplicate);
    }

    public enum LegacyWriterRouteShape
    {
        NoRoute,
        TerminalWarming,
        LegacyActive,
    }

    [Theory]
    [InlineData(LegacyWriterRouteShape.NoRoute)]
    [InlineData(LegacyWriterRouteShape.TerminalWarming)]
    [InlineData(LegacyWriterRouteShape.LegacyActive)]
    public async Task ProjectAsync_LegacyWrite_ShouldBeByteIdenticalToSharedMapperAtSameVersionIncludingRoute(
        LegacyWriterRouteShape shape)
    {
        var dispatcher = new RecordingStatusDispatcher();
        var clock = new FixedProjectionClock(new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero));
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
            StatusRoute = shape switch
            {
                LegacyWriterRouteShape.NoRoute => null,
                LegacyWriterRouteShape.TerminalWarming => ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
                    routeEpoch: 2,
                    ProjectionScopeStatusRoutePhase.Warming),
                LegacyWriterRouteShape.LegacyActive => ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 3),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            },
        };
        state.HighestSeenVersionsByActor["actor-alpha"] = 12;
        state.LastSuccessfulVersionsByActor["actor-alpha"] = 11;
        state.Failures.Add(new ProjectionScopeFailure { FailureId = "failure-1", Reason = "boom" });
        var envelope = CreateCommittedEnvelope(state, version: 7, eventId: "state-event-7");

        await sut.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = "root-actor" },
            envelope);

        CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var unpackedState)
            .Should().BeTrue();
        var mapped = ProjectionScopeStatusDocumentMapper.Map(
            unpackedState!,
            stateEvent!,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, clock.UtcNow));
        var legacyWrite = dispatcher.Upserts.Should().ContainSingle().Which;
        legacyWrite.ToByteString().Equals(mapped.ToByteString()).Should().BeTrue();
        legacyWrite.StatusRoute.Should().Be(state.StatusRoute);
        ((IProjectionRouteFencedReadModel)legacyWrite).RouteEpoch.Should().Be(state.StatusRoute?.RouteEpoch ?? 0);
        ProjectionWriteResultEvaluator.Evaluate(legacyWrite, mapped).Disposition
            .Should().Be(ProjectionWriteDisposition.Duplicate, "the terminal writer's same-version write under the same route must be an exact duplicate, never a conflict");
        ProjectionWriteResultEvaluator.Evaluate(mapped, legacyWrite).Disposition
            .Should().Be(ProjectionWriteDisposition.Duplicate);
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
