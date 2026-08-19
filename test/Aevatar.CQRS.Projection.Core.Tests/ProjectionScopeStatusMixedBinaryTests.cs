using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// Mixed-binary status writers on one source projection scope (#3476), verified against a
/// REAL old-binary fixture: <see cref="LegacyBinaryProjectionScopeStatusProjector"/> is the
/// verbatim <c>ProjectionScopeStatusProjector</c> of commit b64c96a45, the last production
/// binary before the terminal status materializer. Its mapping is copied field by field
/// (including the ordering of <c>SourceVersions</c> and <c>LastSuccessfulSourceCoordinates</c>);
/// only the class name changed and the <c>[ProjectionExempt]</c> registration marker was
/// dropped, because a test fixture is not a registered production materializer. It does not
/// know <c>ProjectionScopeState.status_route</c> (an old binary carries that field of the
/// source state as an unknown field and never reads it) and it writes without any route gate.
///
/// Contract diff between b64c96a45 and the current binary, confirmed with
/// <c>git show b64c96a45:src/Aevatar.CQRS.Projection.Core/projection_scope_status_readmodel.proto</c>:
/// the ONLY difference of <c>ProjectionScopeStatusDocument</c> is that field 30 <c>status_route</c>
/// was added (0049f92e7) and then reserved again (83b01ceb9): <c>reserved 30; reserved "status_route";</c>.
/// Every field an old binary serializes (1..29) is unchanged, so both binaries share one wire
/// layout of the document. On the source side <c>ProjectionScopeState.status_route = 29</c> and the
/// <c>ProjectionScopeStatusRoute</c> message were added; the mapper never copies them into the
/// document. The mapping helpers both binaries call (<c>CommittedStateEventEnvelope</c>,
/// <c>ProjectionScopeActorId</c>, <c>ProjectionScopeModeMapper</c>, <c>ProjectionScopeFailureLog</c>,
/// the document partial) are byte-for-byte unchanged since b64c96a45.
///
/// Rolling design verified here:
/// <list type="bullet">
/// <item><b>Rolling forward</b>: old-binary legacy shadows keep writing every version through the
/// pre-gate mapping; once the fleet gate opens the source commits a terminal route and the terminal
/// materializer writes from the flip version. Any overlap at one version is an exact duplicate.</item>
/// <item><b>Delayed legacy delivery</b>: an old-binary write that lands after the terminal already
/// covered that version is a duplicate (same version) or stale (lower version); an old-binary write
/// for a version the terminal has not processed yet is applied and the terminal's later write for
/// the same event is the duplicate. The store always holds the byte image of the highest version.</item>
/// <item><b>Rollback / drain</b>: rolling back to the old binary orphans the terminal materializer;
/// the re-ensured old-binary legacy shadow re-writes the last version as a duplicate and continues;
/// rolling forward again, the terminal re-writes as duplicate and continues. Never a conflict.</item>
/// <item><b>Sanity</b>: a genuinely different fact at the same version (different event id, or the
/// same event id with different state bytes) is still a conflict; byte equality is not relaxed.</item>
/// </list>
/// </summary>
public sealed class ProjectionScopeStatusMixedBinaryTests
{
    private const string RootActorId = "root-actor";
    private const string ProjectionKind = "mixed-binary-kind";

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    private static readonly string SourceScopeActorId = ProjectionScopeActorId.Build(
        new ProjectionRuntimeScopeKey(RootActorId, ProjectionKind, ProjectionRuntimeMode.DurableMaterialization));

    private static readonly string TerminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(SourceScopeActorId);
    private static readonly string LegacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(SourceScopeActorId);

    // ── 1. byte identity between the old binary and the new mapper ────────────────────

    [Fact]
    public void StatusDocumentDescriptor_KeepsTheLegacyBinaryLayout_AndReservesFormerStatusRouteField30()
    {
        var descriptor = ProjectionScopeStatusDocument.Descriptor;

        descriptor.FindFieldByNumber(30).Should().BeNull("field 30 status_route is reserved; the document never carries a route");
        descriptor.FindFieldByName("status_route").Should().BeNull();
        descriptor.Fields.InDeclarationOrder().Max(static field => field.FieldNumber)
            .Should().Be(29, "the highest field an old binary (b64c96a45) serializes is materialization_cutover = 29");
        typeof(ProjectionScopeStatusDocument).GetProperty("StatusRoute").Should().BeNull();
    }

    [Fact]
    public async Task OldBinaryLegacyAndNewMapper_ProduceByteIdenticalDocuments_WithAndWithoutTerminalRoute()
    {
        const long version = 7;
        var clock = new FixedProjectionClock(Now);
        var unroutedState = BuildSourceState(version, routed: false);
        var routedState = BuildSourceState(version, routed: true);
        routedState.StatusRoute.Should().NotBeNull();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                routedState.StatusRoute,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue("the routed state carries an active terminal route");

        var legacyUnrouted = await WriteThroughOldBinaryLegacyAsync(unroutedState, version, clock);
        var legacyRouted = await WriteThroughOldBinaryLegacyAsync(routedState, version, clock);
        var mapperUnrouted = MapThroughNewBinary(unroutedState, version);
        var mapperRouted = MapThroughNewBinary(routedState, version);

        var legacyUnroutedBytes = legacyUnrouted.ToByteArray();
        legacyUnroutedBytes.Should().NotBeEmpty();
        legacyRouted.ToByteArray().Should().Equal(legacyUnroutedBytes, "the old binary never reads the source route");
        mapperUnrouted.ToByteArray().Should().Equal(legacyUnroutedBytes, "same (state, stateEvent, timestamp) must give the same bytes on both binaries");
        mapperRouted.ToByteArray().Should().Equal(legacyUnroutedBytes, "the route must not leak into the document on the new binary either");

        // Full mapping surface is exercised, not an empty document.
        mapperRouted.SourceVersions.Select(static s => s.SourceActorId).Should().Equal("actor-alpha", "actor-mike", "actor-zulu");
        mapperRouted.LastSuccessfulSourceCoordinates.Select(static c => c.ActorId).Should().Equal("actor-alpha", "actor-zulu");
        mapperRouted.RecentObservedEnvelopes.Should().HaveCount(2);
        mapperRouted.UnresolvedFailureCount.Should().Be(2);
        mapperRouted.RetryExhaustedFailureCount.Should().Be(1);
        mapperRouted.InFlightSource.Should().NotBeNull();
        mapperRouted.ActiveMaterializationRoute.Should().NotBeNull();
        mapperRouted.MaterializationCutover.Should().NotBeNull();

        foreach (var (existing, incoming) in new[]
                 {
                     (legacyUnrouted, mapperRouted),
                     (mapperRouted, legacyUnrouted),
                     (legacyRouted, mapperUnrouted),
                     (mapperUnrouted, legacyRouted),
                 })
        {
            ProjectionWriteResultEvaluator.Evaluate(existing, incoming).Disposition
                .Should().Be(ProjectionWriteDisposition.Duplicate, "the second writer at one version must see an exact duplicate, never a conflict");
        }
    }

    // ── 2. rolling forward ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollingForward_OldBinaryLegacyThenTerminal_NeverConflicts()
    {
        const long flipVersion = 4;
        var world = new MixedBinaryWorld();

        // Old-binary legacy shadow owns v1..v3 (fleet gate closed, no route on the source).
        for (long version = 1; version <= 3; version++)
            (await world.OldBinaryLegacyWriteAsync(version, routed: false)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.OldBinaryLegacyWriteAsync(3, routed: false)).Should().Be(ProjectionWriteDisposition.Duplicate, "an old-binary redelivery of the current version is a duplicate");
        await world.AssertStoreIsByteImageOfAsync(3, routed: false);

        // The fleet gate opens: the source commits the terminal route at v4. A new-binary legacy
        // shadow (gated) refuses to write; the terminal materializer starts and writes.
        (await world.NewBinaryLegacyWriteAsync(flipVersion, routed: true)).Should().BeNull("the gated new-binary legacy shadow does not write once the source flipped");
        (await world.TerminalObserveAsync(flipVersion, routed: true)).Should().Be(ProjectionWriteDisposition.Applied);
        world.Terminal.Agent.State.Active.Should().BeTrue();
        world.Terminal.Agent.State.SourceScopeActorId.Should().Be(SourceScopeActorId);
        world.Terminal.EventSourcing.Committed.OfType<ProjectionScopeStatusTerminalStartedEvent>()
            .Should().ContainSingle("the first routed publication at the flip starts the terminal");

        // The old binary is still draining: it also produced v4 for the same event before draining.
        (await world.OldBinaryLegacyWriteAsync(flipVersion, routed: true)).Should().Be(ProjectionWriteDisposition.Duplicate, "same state/event at the flip version from the old binary is a duplicate, not a conflict");
        await world.AssertStoreIsByteImageOfAsync(flipVersion, routed: true);

        (await world.TerminalObserveAsync(5, routed: true)).Should().Be(ProjectionWriteDisposition.Applied);

        // Delayed old-binary deliveries of already covered versions.
        (await world.OldBinaryLegacyWriteAsync(3, routed: false)).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryLegacyWriteAsync(flipVersion, routed: true)).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.NewBinaryLegacyWriteAsync(5, routed: true)).Should().BeNull();

        world.AssertNoConflictOrGap();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull("no terminal write was rejected");
        await world.AssertStoreIsByteImageOfAsync(5, routed: true);
        world.Journal.Writes.Select(static w => (w.Writer, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.OldBinaryLegacy, 1, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinaryLegacy, 2, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinaryLegacy, 3, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinaryLegacy, 3, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.Terminal, 4, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinaryLegacy, 4, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.Terminal, 5, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinaryLegacy, 3, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinaryLegacy, 4, ProjectionWriteDisposition.Stale));
    }

    // ── 3. delayed legacy delivery after adoption ───────────────────────────────────────

    [Fact]
    public async Task DelayedOldBinaryLegacyDelivery_AfterTerminalAdoption_NeverConflicts()
    {
        var world = new MixedBinaryWorld();

        // Terminal already adopted and wrote v6.
        (await world.TerminalObserveAsync(6, routed: true)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(6, routed: true);

        // Old binary still alive: its v6 (same event) is a duplicate, its v5 is stale.
        (await world.OldBinaryLegacyWriteAsync(6, routed: true)).Should().Be(ProjectionWriteDisposition.Duplicate);
        await world.AssertStoreIsByteImageOfAsync(6, routed: true);
        (await world.OldBinaryLegacyWriteAsync(5, routed: true)).Should().Be(ProjectionWriteDisposition.Stale);
        await world.AssertStoreIsByteImageOfAsync(6, routed: true);

        // The old binary reaches v7 before the terminal processed it: applied; the terminal's v7
        // for the same event is then the duplicate.
        (await world.OldBinaryLegacyWriteAsync(7, routed: true)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(7, routed: true);
        (await world.TerminalObserveAsync(7, routed: true)).Should().Be(ProjectionWriteDisposition.Duplicate);
        await world.AssertStoreIsByteImageOfAsync(7, routed: true);

        world.AssertNoConflictOrGap();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull("a duplicate is not a rejected write");
        world.Terminal.Agent.State.Active.Should().BeTrue();
        world.Journal.Writes.Select(static w => (w.Writer, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.Terminal, 6, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinaryLegacy, 6, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.OldBinaryLegacy, 5, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinaryLegacy, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 7, ProjectionWriteDisposition.Duplicate));
    }

    // ── 4. rollback / drain ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RollbackToOldBinaryThenRollForward_NeverConflicts()
    {
        var world = new MixedBinaryWorld();

        // New binary live: terminal wrote up to v8.
        for (long version = 6; version <= 8; version++)
            (await world.TerminalObserveAsync(version, routed: true)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(8, routed: true);

        // Rollback: the deployment returns to the old binary. The terminal materializer is
        // orphaned (represented by not invoking it). The re-ensured old-binary legacy shadow
        // re-delivers v8 (same event) and continues with v9. The source state still carries the
        // committed route as an unknown field on the old binary; the old binary ignores it.
        (await world.OldBinaryLegacyWriteAsync(8, routed: true)).Should().Be(ProjectionWriteDisposition.Duplicate);
        await world.AssertStoreIsByteImageOfAsync(8, routed: true);
        (await world.OldBinaryLegacyWriteAsync(9, routed: true)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(9, routed: true);

        // Roll forward again: the terminal (relay still durable) re-observes v9 and continues.
        (await world.TerminalObserveAsync(9, routed: true)).Should().Be(ProjectionWriteDisposition.Duplicate);
        await world.AssertStoreIsByteImageOfAsync(9, routed: true);
        (await world.TerminalObserveAsync(10, routed: true)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(10, routed: true);

        // A last old-binary straggler draining after the roll-forward.
        (await world.OldBinaryLegacyWriteAsync(9, routed: true)).Should().Be(ProjectionWriteDisposition.Stale);
        await world.AssertStoreIsByteImageOfAsync(10, routed: true);

        world.AssertNoConflictOrGap();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        world.Journal.Writes.Select(static w => (w.Writer, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.Terminal, 6, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 8, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinaryLegacy, 8, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.OldBinaryLegacy, 9, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 9, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.Terminal, 10, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinaryLegacy, 9, ProjectionWriteDisposition.Stale));
    }

    // ── 5. sanity: byte equality is not relaxed ─────────────────────────────────────────

    [Theory]
    [InlineData(StatusWriter.OldBinaryLegacy)]
    [InlineData(StatusWriter.Terminal)]
    public async Task SameVersionDifferentEventIds_FromTheTwoWriters_IsStillAConflict(StatusWriter firstWriter)
    {
        const long version = 3;
        var world = new MixedBinaryWorld();

        if (firstWriter == StatusWriter.OldBinaryLegacy)
        {
            (await world.OldBinaryLegacyWriteAsync(version, routed: true, eventId: "source-evt-3-from-old-binary"))
                .Should().Be(ProjectionWriteDisposition.Applied);
            (await world.TerminalObserveAsync(version, routed: true, eventId: "source-evt-3-different-fact"))
                .Should().Be(ProjectionWriteDisposition.Conflict);
            var pending = world.Terminal.Agent.State.PendingWrite;
            pending.Should().NotBeNull("the terminal records a rejected write as an explicit failure state");
            pending!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
            pending.Source.StateVersion.Should().Be(version);
            pending.Source.EventId.Should().Be("source-evt-3-different-fact");
        }
        else
        {
            (await world.TerminalObserveAsync(version, routed: true, eventId: "source-evt-3-from-terminal"))
                .Should().Be(ProjectionWriteDisposition.Applied);
            (await world.OldBinaryLegacyWriteAsync(version, routed: true, eventId: "source-evt-3-different-fact"))
                .Should().Be(ProjectionWriteDisposition.Conflict);
            world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        }

        world.Journal.Writes.Should().Contain(static w => w.Disposition == ProjectionWriteDisposition.Conflict);
        world.Journal.Writes.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Gap);
        world.Journal.Writes.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Duplicate);
    }

    [Fact]
    public async Task SameVersionSameEventIdDifferentStateBytes_IsStillAConflict()
    {
        const long version = 3;
        var world = new MixedBinaryWorld();

        (await world.OldBinaryLegacyWriteAsync(version, routed: false)).Should().Be(ProjectionWriteDisposition.Applied);

        var divergedState = BuildSourceState(version, routed: true);
        divergedState.LastSuccessfulVersion += 1;
        (await world.TerminalObserveStateAsync(divergedState, version, DefaultEventId(version)))
            .Should().Be(ProjectionWriteDisposition.Conflict, "same version and event id but different bytes is a different fact");

        world.Terminal.Agent.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        await world.AssertStoreIsByteImageOfAsync(version, routed: false);
    }

    // ── source state / envelope builders ────────────────────────────────────────────────

    private static string DefaultEventId(long version) => $"source-evt-{version}";

    /// <summary>
    /// A rich source state so the full mapping surface (failure summary, per-actor maps in
    /// non-sorted insertion order, coordinates, in-flight source, routes, cutover) is covered.
    /// </summary>
    private static ProjectionScopeState BuildSourceState(long version, bool routed)
    {
        var state = new ProjectionScopeState
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            Released = false,
            ObservationAttached = true,
            ActivationGeneration = 1,
            HighestSeenVersion = version * 10,
            LastSuccessfulVersion = version * 10 - 1,
            ReceivedEnvelopeTotal = version * 3,
            AttemptedEnvelopeTotal = version * 3 - 1,
            SuccessfulMaterializationTotal = version * 2,
            FailedAttemptTotal = 2,
            RetryExhaustedTotal = 1,
            FailureDiagnosticDroppedTotal = 4,
        };

        // Insertion order deliberately not sorted: the mapping orders by actor id.
        state.HighestSeenVersionsByActor["actor-zulu"] = version * 10;
        state.HighestSeenVersionsByActor["actor-alpha"] = version * 10 - 2;
        state.LastSuccessfulVersionsByActor["actor-zulu"] = version * 10 - 1;
        state.LastSuccessfulVersionsByActor["actor-mike"] = version;
        state.LastSuccessfulSourceCoordinatesByActor["actor-zulu"] = new ProjectionSourceCoordinate
        {
            ActorId = "actor-zulu",
            StateVersion = version * 10 - 1,
            EventId = $"zulu-evt-{version * 10 - 1}",
        };
        state.LastSuccessfulSourceCoordinatesByActor["actor-alpha"] = new ProjectionSourceCoordinate
        {
            ActorId = "actor-alpha",
            StateVersion = version * 10 - 3,
            EventId = $"alpha-evt-{version * 10 - 3}",
        };
        state.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-1",
            Stage = "materialize",
            Reason = "boom",
            RetryExhausted = true,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now.AddMinutes(-10)),
        });
        state.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-2",
            Stage = "materialize",
            Reason = "boom again",
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now.AddMinutes(-5)),
        });
        state.RecentObservedEnvelopes.Add(new ProjectionObservedEnvelopeMetadata
        {
            EventId = $"zulu-evt-{version * 10 - 1}",
            TypeUrl = "type.googleapis.com/aevatar.SourceEvent",
            StateVersion = version * 10 - 1,
            TimestampUtc = Timestamp.FromDateTimeOffset(Now.AddSeconds(version - 1)),
        });
        state.RecentObservedEnvelopes.Add(new ProjectionObservedEnvelopeMetadata
        {
            EventId = $"zulu-evt-{version * 10}",
            TypeUrl = "type.googleapis.com/aevatar.SourceEvent",
            StateVersion = version * 10,
            TimestampUtc = Timestamp.FromDateTimeOffset(Now.AddSeconds(version)),
        });
        state.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "actor-zulu",
                StateVersion = version * 10,
                EventId = $"zulu-evt-{version * 10}",
            },
            Envelope = new EventEnvelope
            {
                Id = "in-flight",
                Payload = Any.Pack(new StringValue { Value = "must-not-project" }),
            },
            EventKind = "source-event",
            StagedAtUtc = Timestamp.FromDateTimeOffset(Now.AddSeconds(version)),
        };
        state.ActiveMaterializationRoute = new ProjectionMaterializationRouteFingerprint
        {
            ContractId = "workflow-run-graph",
            ContractVersion = 2,
            PhysicalNamespace = "workflow-execution-graph-v2",
            RouteEpoch = 4,
        };
        state.MaterializationCutover = new ProjectionMaterializationCutoverState
        {
            Phase = ProjectionMaterializationCutoverPhase.Activated,
            CandidateRoute = state.ActiveMaterializationRoute.Clone(),
            CandidateFingerprint = "fp-4",
            UpdatedAtUtc = Timestamp.FromDateTimeOffset(Now.AddMinutes(-30)),
        };

        if (routed)
        {
            state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1);
            state.StatusRoute.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
            state.StatusRoute.LegacyRouteReleased = true;
        }

        return state;
    }

    private static Timestamp StateEventTimestamp(long version) =>
        Timestamp.FromDateTimeOffset(Now.AddSeconds(version));

    private static StateEvent BuildStateEvent(ProjectionScopeState state, long version, string eventId) =>
        new()
        {
            AgentId = SourceScopeActorId,
            EventId = eventId,
            Version = version,
            EventType = ProjectionScopeWatermarkAdvancedEvent.Descriptor.FullName,
            Timestamp = StateEventTimestamp(version),
            EventData = Any.Pack(new ProjectionScopeWatermarkAdvancedEvent
            {
                LastSuccessfulVersion = state.LastSuccessfulVersion,
            }),
        };

    /// <summary>The source scope's committed publication as forwarded by its relay to one status writer.</summary>
    private static EventEnvelope BuildCommittedSourceEnvelope(
        string targetActorId,
        ProjectionScopeState state,
        long version,
        string eventId)
    {
        var original = new EventEnvelope
        {
            Id = $"outer-{targetActorId}-{version}-{eventId}",
            Timestamp = StateEventTimestamp(version),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(SourceScopeActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = BuildStateEvent(state, version, eventId),
                StateRoot = Any.Pack(state),
            }),
        };

        return StreamForwardingRules.BuildForwardedEnvelope(
            original,
            sourceStreamId: SourceScopeActorId,
            targetStreamId: targetActorId,
            StreamForwardingMode.HandleThenForward);
    }

    private static async Task<ProjectionScopeStatusDocument> WriteThroughOldBinaryLegacyAsync(
        ProjectionScopeState state,
        long version,
        IProjectionClock clock)
    {
        var dispatcher = new RecordingOnlyStatusDispatcher();
        var legacy = new LegacyBinaryProjectionScopeStatusProjector(dispatcher, clock);
        await legacy.ProjectAsync(
            new ProjectionScopeStatusMaterializationContext { RootActorId = SourceScopeActorId },
            BuildCommittedSourceEnvelope(LegacyActorId, state, version, DefaultEventId(version)));
        return dispatcher.Upserts.Should().ContainSingle().Which;
    }

    private static ProjectionScopeStatusDocument MapThroughNewBinary(ProjectionScopeState state, long version)
    {
        var envelope = BuildCommittedSourceEnvelope(TerminalActorId, state, version, DefaultEventId(version));
        CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var unpackedState)
            .Should().BeTrue();
        return ProjectionScopeStatusDocumentMapper.Map(
            unpackedState!,
            stateEvent!,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, Now));
    }

    // ── the mixed-binary world: one store, three writers ────────────────────────────────

    public enum StatusWriter
    {
        OldBinaryLegacy,
        NewBinaryLegacy,
        Terminal,
    }

    private sealed record StatusWrite(
        StatusWriter Writer,
        long Version,
        string EventId,
        ProjectionWriteDisposition Disposition);

    /// <summary>
    /// The one status document store shared by every writer of the rolling window, plus the
    /// ordered journal of every write it received.
    /// </summary>
    private sealed class StatusStoreJournal
    {
        public InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string> Store { get; } =
            new(static document => document.Id);

        public List<StatusWrite> Writes { get; } = [];

        public IProjectionWriteDispatcher<ProjectionScopeStatusDocument> DispatcherFor(StatusWriter writer) =>
            new JournalingStatusDispatcher(this, writer);

        private sealed class JournalingStatusDispatcher : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
        {
            private readonly StatusStoreJournal _journal;
            private readonly StatusWriter _writer;

            public JournalingStatusDispatcher(StatusStoreJournal journal, StatusWriter writer)
            {
                _journal = journal;
                _writer = writer;
            }

            public async Task<ProjectionWriteResult> UpsertAsync(
                ProjectionScopeStatusDocument readModel,
                CancellationToken ct = default)
            {
                var result = await _journal.Store.UpsertAsync(readModel, ct);
                _journal.Writes.Add(new StatusWrite(_writer, readModel.StateVersion, readModel.LastEventId, result.Disposition));
                return result;
            }

            public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
                throw new NotSupportedException("status writers never delete");
        }
    }

    private sealed class MixedBinaryWorld
    {
        private readonly FixedProjectionClock _clock = new(Now);
        private readonly LegacyBinaryProjectionScopeStatusProjector _oldBinaryLegacy;
        private readonly ProjectionScopeStatusProjector _newBinaryLegacy;
        private readonly ProjectionScopeStatusMaterializationContext _legacyContext =
            new() { RootActorId = SourceScopeActorId };

        public MixedBinaryWorld()
        {
            _oldBinaryLegacy = new LegacyBinaryProjectionScopeStatusProjector(
                Journal.DispatcherFor(StatusWriter.OldBinaryLegacy),
                _clock);
            _newBinaryLegacy = new ProjectionScopeStatusProjector(
                Journal.DispatcherFor(StatusWriter.NewBinaryLegacy),
                _clock);
            Terminal = TerminalAgentHarness.Build(Journal.DispatcherFor(StatusWriter.Terminal), _clock);
        }

        public StatusStoreJournal Journal { get; } = new();
        public TerminalAgentHarness Terminal { get; }

        /// <summary>Old-binary legacy shadow: pre-gate binary, writes every delivered version.</summary>
        public async Task<ProjectionWriteDisposition> OldBinaryLegacyWriteAsync(
            long version,
            bool routed,
            string? eventId = null)
        {
            var before = Journal.Writes.Count;
            await _oldBinaryLegacy.ProjectAsync(
                _legacyContext,
                BuildCommittedSourceEnvelope(LegacyActorId, BuildSourceState(version, routed), version, eventId ?? DefaultEventId(version)));
            return SingleNewWrite(before, StatusWriter.OldBinaryLegacy).Disposition;
        }

        /// <summary>New-binary legacy shadow: gated by the source's committed route; null when it did not write.</summary>
        public async Task<ProjectionWriteDisposition?> NewBinaryLegacyWriteAsync(
            long version,
            bool routed,
            string? eventId = null)
        {
            var before = Journal.Writes.Count;
            await _newBinaryLegacy.ProjectAsync(
                _legacyContext,
                BuildCommittedSourceEnvelope(LegacyActorId, BuildSourceState(version, routed), version, eventId ?? DefaultEventId(version)));
            return Journal.Writes.Count == before ? null : SingleNewWrite(before, StatusWriter.NewBinaryLegacy).Disposition;
        }

        /// <summary>The real terminal materializer observing the source's forwarded publication.</summary>
        public Task<ProjectionWriteDisposition?> TerminalObserveAsync(long version, bool routed, string? eventId = null) =>
            TerminalObserveStateAsync(BuildSourceState(version, routed), version, eventId ?? DefaultEventId(version));

        public async Task<ProjectionWriteDisposition?> TerminalObserveStateAsync(
            ProjectionScopeState state,
            long version,
            string eventId)
        {
            var before = Journal.Writes.Count;
            await Terminal.Agent.HandleObservedEnvelopeAsync(
                BuildCommittedSourceEnvelope(TerminalActorId, state, version, eventId));
            return Journal.Writes.Count == before ? null : SingleNewWrite(before, StatusWriter.Terminal).Disposition;
        }

        public void AssertNoConflictOrGap()
        {
            Journal.Writes.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Conflict);
            Journal.Writes.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Gap);
        }

        /// <summary>The store holds exactly the byte image of the given version, whoever wrote it.</summary>
        public async Task AssertStoreIsByteImageOfAsync(long version, bool routed)
        {
            var stored = await Journal.Store.GetAsync(SourceScopeActorId);
            stored.Should().NotBeNull();
            stored!.StateVersion.Should().Be(version);
            stored.LastEventId.Should().Be(DefaultEventId(version));
            var expected = MapThroughNewBinary(BuildSourceState(version, routed), version);
            stored.ToByteArray().Should().Equal(expected.ToByteArray());
        }

        private StatusWrite SingleNewWrite(int before, StatusWriter writer)
        {
            Journal.Writes.Should().HaveCount(before + 1, $"{writer} performs exactly one write per delivered envelope");
            var write = Journal.Writes[^1];
            write.Writer.Should().Be(writer);
            return write;
        }
    }

    // ── terminal materializer harness (real ProjectionScopeStatusGAgent) ────────────────

    private sealed class TerminalAgentHarness
    {
        public required ProjectionScopeStatusGAgent Agent { get; init; }
        public required TrackingEventSourcing<ProjectionScopeStatusTerminalState> EventSourcing { get; init; }

        public static TerminalAgentHarness Build(
            IProjectionWriteDispatcher<ProjectionScopeStatusDocument> dispatcher,
            IProjectionClock clock)
        {
            var agent = new ProjectionScopeStatusGAgent();
            typeof(GAgentBase)
                .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(agent, TerminalActorId);
            var eventSourcing = new TrackingEventSourcing<ProjectionScopeStatusTerminalState>(TransitionTerminal);
            agent.EventSourcing = eventSourcing;

            var services = new ServiceCollection();
            services.AddSingleton(dispatcher);
            services.AddSingleton(clock);
            services.AddSingleton<IStreamProvider>(new NoRelayStreamProvider());
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            agent.Services = services.BuildServiceProvider();
            return new TerminalAgentHarness { Agent = agent, EventSourcing = eventSourcing };
        }

        private static ProjectionScopeStatusTerminalState TransitionTerminal(
            ProjectionScopeStatusTerminalState current,
            IMessage evt) =>
            evt switch
            {
                ProjectionScopeStatusTerminalStartedEvent started =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyStarted(current, started),
                ProjectionScopeStatusTerminalReleasedEvent released =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyReleased(current, released),
                ProjectionScopeStatusWriteDeferredEvent deferred =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyWriteDeferred(current, deferred),
                ProjectionScopeStatusWriteRecoveredEvent recovered =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyWriteRecovered(current, recovered),
                ProjectionScopeStatusWriteStalledEvent stalled =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyWriteStalled(current, stalled),
                _ => current,
            };
    }

    private sealed class TrackingEventSourcing<TState> : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly List<IMessage> _pending = [];
        private readonly Func<TState, IMessage, TState> _transition;

        public TrackingEventSourcing(Func<TState, IMessage, TState> transition)
        {
            _transition = transition;
        }

        public List<IMessage> Committed { get; } = [];
        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
                CurrentVersion++;
                Committed.Add(evt);
                result.CommittedEvents.Add(new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(Now),
                    Version = CurrentVersion,
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                });
            }

            result.LatestVersion = CurrentVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<TState?>(null);

        public void DiscardPendingEvents() => _pending.Clear();

        public TState TransitionState(TState current, IMessage evt) => _transition(current, evt);
    }

    /// <summary>The terminal's relay lifecycle is not under test here; relays are accepted and forgotten.</summary>
    private sealed class NoRelayStreamProvider : IStreamProvider
    {
        public IStream GetStream(string actorId) => new NoRelayStream(actorId);

        private sealed class NoRelayStream : IStream
        {
            public NoRelayStream(string streamId)
            {
                StreamId = streamId;
            }

            public string StreamId { get; }

            public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage =>
                throw new NotSupportedException();

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : IMessage, new() =>
                throw new NotSupportedException();

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FixedProjectionClock : IProjectionClock
    {
        public FixedProjectionClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingOnlyStatusDispatcher : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
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

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

/// <summary>
/// VERBATIM fixture: <c>src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusProjector.cs</c>
/// as of commit b64c96a45 (<c>git show b64c96a45:...</c>), the last production binary before the
/// terminal status materializer. Only the class/constructor name differs and the
/// <c>[ProjectionExempt]</c> registration marker is omitted (a test fixture is not a registered
/// production materializer). It builds the document field by field, in the same order, and
/// writes it unconditionally: it has no notion of <c>ProjectionScopeState.status_route</c>
/// (an unknown field to that binary) and no route gate. Do not "modernize" this class; its
/// value is that it is the old binary.
/// </summary>
internal sealed class LegacyBinaryProjectionScopeStatusProjector
    : ICurrentStateProjectionMaterializer<ProjectionScopeStatusMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ProjectionScopeStatusDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public LegacyBinaryProjectionScopeStatusProjector(
        IProjectionWriteDispatcher<ProjectionScopeStatusDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ProjectionScopeStatusMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var scopeKey = new ProjectionRuntimeScopeKey(
            state.RootActorId,
            state.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(state.Mode),
            state.SessionId);
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        var failureSummary = state.FailureSummary ?? ProjectionScopeFailureLog.BuildSummary(state.Failures);

        var document = new ProjectionScopeStatusDocument
        {
            Id = scopeActorId,
            ScopeActorId = scopeActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(updatedAt.ToUniversalTime()),
            RootActorId = state.RootActorId ?? string.Empty,
            ProjectionKind = state.ProjectionKind ?? string.Empty,
            SessionId = state.SessionId ?? string.Empty,
            Mode = state.Mode,
            Active = state.Active,
            ObservationAttached = state.ObservationAttached,
            Released = state.Released,
            HighestSeenVersion = state.HighestSeenVersion,
            LastSuccessfulVersion = state.LastSuccessfulVersion,
            UnresolvedFailureCount = failureSummary.UnresolvedFailureCount,
            ReceivedEnvelopeTotal = state.ReceivedEnvelopeTotal,
            AttemptedEnvelopeTotal = state.AttemptedEnvelopeTotal,
            SuccessfulMaterializationTotal = state.SuccessfulMaterializationTotal,
            FailedAttemptTotal = state.FailedAttemptTotal,
            RetryExhaustedTotal = state.RetryExhaustedTotal,
            RetryExhaustedFailureCount = failureSummary.RetryExhaustedFailureCount,
            FailureDiagnosticDroppedTotal = state.FailureDiagnosticDroppedTotal,
            InFlightSource = state.InFlightObservation?.Source?.Clone(),
            ActiveMaterializationRoute = state.ActiveMaterializationRoute?.Clone(),
            MaterializationCutover = state.MaterializationCutover?.Clone(),
        };
        document.OldestUnresolvedFailureAtUtc = failureSummary.OldestUnresolvedFailureAtUtc?.Clone();
        document.RecentObservedEnvelopes.Add(state.RecentObservedEnvelopes);
        document.LastSuccessfulSourceCoordinates.Add(
            state.LastSuccessfulSourceCoordinatesByActor.Values
                .OrderBy(static source => source.ActorId, StringComparer.Ordinal)
                .Select(static source => source.Clone()));
        foreach (var sourceActorId in state.HighestSeenVersionsByActor.Keys
                     .Union(state.LastSuccessfulVersionsByActor.Keys, StringComparer.Ordinal)
                     .OrderBy(sourceActorId => sourceActorId, StringComparer.Ordinal))
        {
            var highestSeen = state.HighestSeenVersionsByActor.TryGetValue(sourceActorId, out var seen)
                ? seen
                : 0;
            var lastSuccessful = state.LastSuccessfulVersionsByActor.TryGetValue(sourceActorId, out var successful)
                ? successful
                : 0;
            document.SourceVersions.Add(new ProjectionSourceVersionStatus
            {
                SourceActorId = sourceActorId,
                HighestSeenVersion = highestSeen,
                LastSuccessfulVersion = lastSuccessful,
                VersionGap = Math.Max(0, highestSeen - lastSuccessful),
            });
        }

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
