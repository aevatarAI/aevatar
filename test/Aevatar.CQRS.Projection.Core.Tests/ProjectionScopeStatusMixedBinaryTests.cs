using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// Mixed-binary status writers on one source projection scope (#3476), verified against REAL
/// old-binary fixtures. Four binaries can write <see cref="ProjectionScopeStatusDocument"/> for
/// the same source scope during a rolling upgrade or rollback:
/// <list type="bullet">
/// <item><b>B0 = b64c96a45</b> (last binary before the terminal status materializer):
/// <see cref="LegacyBinaryProjectionScopeStatusProjector"/> is its verbatim
/// <c>ProjectionScopeStatusProjector</c>. It writes every delivered version, knows no route and
/// writes a document WITHOUT field 30 (route epoch 0). Its write evaluator was the plain byte
/// evaluator (<see cref="PreviousBinaryProjectionWriteResultEvaluator"/>).</item>
/// <item><b>B1 = 8d47b5e5c</b> (terminal materializer, document carried <c>status_route = 30</c>):
/// <see cref="PreviousBinaryProjectionScopeStatusMapper"/> is its verbatim
/// <c>ProjectionScopeStatusDocumentMapper</c> (it copies the state route into the document, exactly
/// like today's mapper) and <see cref="PreviousBinaryProjectionScopeStatusTerminalWriter"/> is its
/// terminal write gate (contract id match AND <c>ContractVersion == 1</c>, phase-less routes). Its
/// routes were v1: contract version 1, epoch 1, no phase. Its evaluator was the plain byte
/// evaluator.</item>
/// <item><b>B2 = 416e80f4a</b> (document had NO route field: <c>reserved 30</c>): every write of
/// that binary, legacy or terminal, has exactly B0's bytes even for a routed state, so B2 is
/// represented by the B0 fixture fed a routed state. Its terminal gate was B1's (<c>== 1</c>), its
/// legacy projector skipped every terminal route, its evaluator was the plain byte evaluator.</item>
/// <item><b>B3 = this worktree</b>: the document carries <c>status_route</c> again and implements
/// <see cref="IProjectionRouteFencedReadModel"/> (route epoch 0 when absent);
/// <see cref="ProjectionWriteResultEvaluator"/> is epoch fenced at the same source version; the
/// legacy shadow projector is gated by <c>ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite</c>;
/// phased route model is WARMING, caught up, BLOCKED, previous writer released, ACTIVE. Routes
/// remain <c>aevatar.projection.scope-status-terminal.v2</c> / reader 2 and earlier v1 routes remain
/// serviceable, but the Phase-A binary advertises only the distinct quiescence bridge contract at
/// reader 3. It never creates, upgrades, or rolls back a route; persisted WARMING/BLOCKED routes
/// freeze until exact bridge unanimity yields typed quiescence evidence, then resume.</item>
/// </list>
/// Provenance commands: <c>git show b64c96a45:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusProjector.cs</c>,
/// <c>git show 8d47b5e5c:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusDocumentMapper.cs</c>,
/// <c>git show 8d47b5e5c:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusRoutePolicy.cs</c>,
/// <c>git show 416e80f4a:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs</c> (the old
/// SOURCE scope: <c>AdoptTerminalStatusRouteAsync</c>, <c>ReleaseLegacyStatusRouteIfPendingAsync</c>,
/// <c>TryReleaseLegacyStatusShadowOfFlippedSourceAsync</c>),
/// <c>git show 416e80f4a:src/Aevatar.CQRS.Projection.Core/Orchestration/{ProjectionScopeStatusRoutePolicy,ProjectionScopeStatusProjector,ProjectionScopeStatusTerminalCapabilityAdvertisement}.cs</c>,
/// <c>git show 416e80f4a:src/Aevatar.CQRS.Projection.Core/projection_scope_status_readmodel.proto</c>,
/// <c>git show {b64c96a45,8d47b5e5c,416e80f4a}:src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/ReadModels/ProjectionWriteResultEvaluator.cs</c>
/// (byte-identical in all three: one plain evaluator). Fields 1..29 of the document are unchanged
/// since b64c96a45; the only wire difference across the four binaries is whether field 30 is
/// written. The mapping helpers every binary calls (<c>CommittedStateEventEnvelope</c>,
/// <c>ProjectionScopeActorId</c>, <c>ProjectionScopeModeMapper</c>, <c>ProjectionScopeFailureLog</c>,
/// the document partial) are unchanged since b64c96a45.
///
/// Rolling design verified here (every writer goes through ONE shared store; the current store's
/// evaluator decides unless a step says "old evaluator", which models what the old binary itself
/// would log against the same store — for a same-version write the old evaluator's Conflict and
/// the current evaluator's Stale both leave the store untouched, the difference is the log line.
/// Against Elasticsearch the old binary parses the stored document with unknown fields ignored
/// (<c>JsonParser.Settings.Default.WithIgnoreUnknownFields(true)</c> in every binary since
/// b64c96a45), so it does not even see field 30 and logs Duplicate rather than Conflict; the
/// protobuf-level Conflict asserted below is the strictest reading of the old side):
/// <list type="bullet">
/// <item><b>Persisted cutover recovery</b>: after typed bridge quiescence, epoch-0 documents are
/// taken over at the same version by the persisted route's epoch-1 writer; delayed old writes are
/// stale or duplicate. These fixtures exercise recovery and store fencing only. Phase A never
/// initiates the WARMING or rollback routes they feed through the real appliers.</item>
/// <item><b>Bridge contract</b>: Phase-A binaries no longer advertise the V2 live-admission
/// contract. Reader-2 V2 and reader-3 bridge members cannot form exact unanimity, so mixed fleets
/// close V2 OPEN and writers emit no old-source-compatible continuation. Two historical hazards
/// remain explicit: an old shadow can release itself on any routed source, and an old source with
/// a fresh v1 grant can adopt a higher-epoch v1 route. That the gates stay shut while the fleet is
/// mixed, and typed QUIESCED appears only after bridge unanimity, is proven against the authority in
/// <c>test/Aevatar.Foundation.Core.Tests/RuntimeFleetCapabilityAuthorityTests.cs</c>
///; this file asserts the projection-side advertisement and quiescence policy inputs.</item>
/// <item><b>Delayed legacy delivery</b>: an old-binary write for a version the store already holds
/// under a higher epoch is Stale under the current evaluator (Conflict only in the old binary's
/// own log, store untouched); a lower version is Stale; a version the new writer has not processed
/// yet is applied and the new writer's later write at that version takes it over (or duplicates
/// it). The store always holds the byte image of the highest version.</item>
/// <item><b>Persisted rollback / drain</b>: an old binary after rollback writes higher versions with a lower
/// epoch (version wins, Applied); its same-version redelivery over a higher-epoch document is
/// Stale/Conflict-in-its-own-log; rolling forward again the new writer takes over at the same
/// version and continues. A rollback route persisted before Phase A can resume after quiescence:
/// terminal writes while legacy warms, releases when it blocks, and legacy writes from BLOCKED on.
/// Phase A itself never creates that rollback or performs an in-place contract upgrade.</item>
/// <item><b>Sanity</b>: under one epoch a genuinely different fact at the same version (different
/// event id, or same event id with different state bytes) is still a Conflict; byte equality is
/// not relaxed inside an epoch.</item>
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

    private const string SourceAgentKind = "projection.materialization-scope.mixed-binary-context";
    private const string LegacyWriterAgentKind =
        "projection.materialization-scope.projection-scope-status-materialization-context";

    // ── 0. document contract ───────────────────────────────────────────────────────────

    [Fact]
    public void StatusDocumentDescriptor_KeepsTheLegacyBinaryLayout_AndCarriesStatusRouteField30Again()
    {
        var descriptor = ProjectionScopeStatusDocument.Descriptor;

        descriptor.FindFieldByNumber(30).Should().NotBeNull("field 30 status_route is carried again (B1 shape)");
        descriptor.FindFieldByNumber(30)!.Name.Should().Be("status_route");
        descriptor.FindFieldByNumber(30)!.MessageType.FullName.Should().Be(ProjectionScopeStatusRoute.Descriptor.FullName);
        descriptor.FindFieldByNumber(29)!.Name.Should().Be("materialization_cutover", "the highest field b64c96a45 serializes");
        descriptor.Fields.InDeclarationOrder().Max(static field => field.FieldNumber).Should().Be(30);
        typeof(IProjectionRouteFencedReadModel).IsAssignableFrom(typeof(ProjectionScopeStatusDocument)).Should().BeTrue();

        IProjectionRouteFencedReadModel unrouted = new ProjectionScopeStatusDocument();
        unrouted.RouteEpoch.Should().Be(0, "documents of binaries that did not carry the route have epoch 0");
        IProjectionRouteFencedReadModel routed = new ProjectionScopeStatusDocument
        {
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 7),
        };
        routed.RouteEpoch.Should().Be(7);
    }

    // ── 1. byte identity within an epoch ───────────────────────────────────────────────

    [Fact]
    public async Task B1AndB3Mappers_ProduceIdenticalBytes_OnTheSameV1Route_AndAreDuplicatesBothWays()
    {
        const long version = 7;
        var route = V1TerminalRoute(epoch: 1);
        var state = BuildSourceState(version, route);
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(route, ProjectionScopeStatusGAgent.ContractId, ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue("the current terminal materializer serves routes of every earlier terminal contract");
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(route)
            .Should().BeTrue("B1 wrote this route");

        var previous = await WriteThroughPreviousBinaryTerminalAsync(state, version, new FixedProjectionClock(Now));
        var current = MapThroughNewBinary(state, version);

        var previousBytes = previous.ToByteArray();
        previousBytes.Should().NotBeEmpty();
        current.ToByteArray().Should().Equal(previousBytes, "same (state, stateEvent, timestamp) gives the same bytes on B1 and B3");
        current.StatusRoute.Should().NotBeNull();
        current.StatusRoute.RouteEpoch.Should().Be(1);
        current.StatusRoute.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
        current.StatusRoute.ContractVersion.Should().Be(PreviousBinaryProjectionScopeStatusTerminalWriter.ReaderVersion);
        AssertFullMappingSurface(current);

        ProjectionWriteResultEvaluator.Evaluate(previous, current).Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
        ProjectionWriteResultEvaluator.Evaluate(current, previous).Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
        PreviousBinaryProjectionWriteResultEvaluator.Evaluate(previous, current).Disposition
            .Should().Be(ProjectionWriteDisposition.Duplicate, "B1's own evaluator agrees on identical bytes");
    }

    [Fact]
    public async Task B0AndB3_ProduceIdenticalBytes_WithoutRoute_AndTheRouteIsTheOnlyDifferenceWithOne()
    {
        const long version = 7;
        var clock = new FixedProjectionClock(Now);
        var unroutedState = BuildSourceState(version, route: null);
        var routedState = BuildSourceState(version, V2TerminalRoute(epoch: 1));

        var oldUnrouted = await WriteThroughOldBinaryLegacyAsync(unroutedState, version, clock);
        var oldRouted = await WriteThroughOldBinaryLegacyAsync(routedState, version, clock);
        var newUnrouted = MapThroughNewBinary(unroutedState, version);
        var newRouted = MapThroughNewBinary(routedState, version);

        var oldBytes = oldUnrouted.ToByteArray();
        oldBytes.Should().NotBeEmpty();
        oldRouted.ToByteArray().Should().Equal(oldBytes, "B0 (and B2) never put the route into the document");
        newUnrouted.ToByteArray().Should().Equal(oldBytes, "without a route B3 writes exactly the B0 bytes");
        AssertFullMappingSurface(newRouted);

        // With a route the ONLY difference is field 30: clearing it restores the B0 bytes.
        var newRoutedWithoutField30 = newRouted.Clone();
        newRoutedWithoutField30.StatusRoute = null;
        newRoutedWithoutField30.ToByteArray().Should().Equal(oldBytes);

        // Same epoch (0): exact duplicates both ways.
        ProjectionWriteResultEvaluator.Evaluate(oldUnrouted, newUnrouted).Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
        ProjectionWriteResultEvaluator.Evaluate(newUnrouted, oldUnrouted).Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);

        // Different epochs (0 vs 1): the fence decides, never a conflict.
        ProjectionWriteResultEvaluator.Evaluate(oldRouted, newRouted).Disposition
            .Should().Be(ProjectionWriteDisposition.Applied, "an epoch-1 write takes an epoch-0 document of the same version over");
        ProjectionWriteResultEvaluator.Evaluate(newRouted, oldRouted).Disposition
            .Should().Be(ProjectionWriteDisposition.Stale, "an epoch-0 write under an epoch-1 document of the same version is stale");
        PreviousBinaryProjectionWriteResultEvaluator.Evaluate(newRouted, oldRouted).Disposition
            .Should().Be(ProjectionWriteDisposition.Conflict, "the old binary's own evaluator logs a conflict for the same pair; its store write is refused either way");
    }

    // ── 3. rolling 416e80f4a (B2) -> B3 on a v1 route, and back ────────────────────────

    [Fact]
    public async Task RollingB2ToB3OnV1Route_TerminalTakesOverTheSameVersion_AndRollbackToB2NeverCorruptsTheStore()
    {
        var route = V1TerminalRoute(epoch: 1); // adopted by B2 (reader version 1), phase-less
        var world = new MixedBinaryWorld();

        // B2's terminal (no-route bytes) wrote v1..v5 before the upgrade.
        for (long version = 1; version <= 5; version++)
            (await world.OldBinaryWriteAsync(Publication(version, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(5, carriedRoute: null);

        // Upgrade to B3: the terminal re-observes v5 (lost ack / duplicate delivery on restart).
        // Epoch 1 > 0: same-version takeover, never a conflict; a repeat is a duplicate.
        (await world.TerminalObserveAsync(Publication(5, route))).Should().Be(ProjectionWriteDisposition.Applied);
        world.Terminal.Agent.State.Active.Should().BeTrue();
        world.Terminal.Agent.State.SourceScopeActorId.Should().Be(SourceScopeActorId);
        world.Terminal.EventSourcing.Committed.OfType<ProjectionScopeStatusTerminalStartedEvent>().Should().ContainSingle();
        await world.AssertStoreIsByteImageOfAsync(5, carriedRoute: route);
        (await world.TerminalObserveAsync(Publication(5, route))).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.TerminalObserveAsync(Publication(6, route))).Should().Be(ProjectionWriteDisposition.Applied);

        // B2 stragglers during the roll: under the current store they are stale; B2's own plain
        // evaluator logs a conflict for the same-version pair (bytes differ by field 30 only) and
        // refuses its own write. Either way the store keeps B3's document.
        (await world.OldBinaryWriteAsync(Publication(5, route))).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(Publication(6, route))).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(Publication(6, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Conflict);
        await world.AssertStoreIsByteImageOfAsync(6, carriedRoute: route);

        // Rollback B3 -> B2: B2 redelivers v6 (its evaluator: conflict, log only; store untouched)
        // and writes v7 (higher version wins, epoch 0 over epoch 1).
        (await world.OldBinaryWriteAsync(Publication(6, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Conflict);
        await world.AssertStoreIsByteImageOfAsync(6, carriedRoute: route);
        (await world.OldBinaryWriteAsync(Publication(7, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(7, carriedRoute: null);

        // Roll forward again: B3's terminal takes v7 over and continues.
        (await world.TerminalObserveAsync(Publication(7, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(7, carriedRoute: route);
        (await world.TerminalObserveAsync(Publication(8, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(8, carriedRoute: route);

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull("a takeover or a duplicate is never a rejected write");
        world.Journal.Writes.Select(static w => (w.Writer, w.Evaluator, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 1, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 2, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 3, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 4, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 6, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 6, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, WriteEvaluator.OldBinaryPlain, 6, ProjectionWriteDisposition.Conflict),
            (StatusWriter.OldBinary, WriteEvaluator.OldBinaryPlain, 6, ProjectionWriteDisposition.Conflict),
            (StatusWriter.OldBinary, WriteEvaluator.OldBinaryPlain, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 8, ProjectionWriteDisposition.Applied));

        // The contract revision draws the line here: only a route an OLD binary created (v1) is
        // shared ground. A source that adopts the terminal writer FRESH under B3 gets a v2 route,
        // and B2's terminal gate (contract id AND reader version exactly 1) matches it in no
        // phase — which is exactly why a fresh adoption may only happen once the v2 gate is
        // unanimous, i.e. once no B2 silo is left in the fleet (see the mixed-fleet tests).
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(V2TerminalRoute(epoch: 1)).Should().BeFalse();
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(V2TerminalRoute(epoch: 2, ProjectionScopeStatusRoutePhase.Blocked)).Should().BeFalse();
    }

    [Fact]
    public async Task RollbackToB2AfterAFreshB3Adoption_NoB2WriterAdoptsTheV2Route_TheDocumentIsFrozenNotCorrupted()
    {
        // The route a B3 phased cutover commits after the contract revision: v2 contract, epoch 1,
        // phase ACTIVE. NEITHER of B2's writers claims it — its terminal gate wants contract id
        // ".v1" at reader version exactly 1, and its legacy projector skips every routed source
        // (416e80f4a IsTerminalRoute is "any non-empty route").
        var route = V2TerminalRoute(epoch: 1);
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(route)
            .Should().BeFalse("the revised contract closes B2's phase-unaware terminal gate");
        PreviousBinaryRouteGatedLegacyProjector.WritesFor(route)
            .Should().BeFalse("B2's legacy shadow projector skips every routed source");

        // Positive control for that fixture, so "B2 has no writer for a v2 route" below is not the
        // same observation as "the fixture cannot write". On a source with NO committed route its
        // gate opens and the write reaches the shared store as B0 bytes; an inverted gate or an
        // early return in its unpack path would fail here instead of passing silently below.
        PreviousBinaryRouteGatedLegacyProjector.WritesFor(route: null).Should().BeTrue();
        var control = new MixedBinaryWorld();
        (await control.PreviousBinaryLegacyWriteAsync(Publication(8, route: null)))
            .Should().Be(ProjectionWriteDisposition.Applied);
        await control.AssertStoreIsByteImageOfAsync(8, carriedRoute: null);
        control.Journal.Writes.Should().ContainSingle()
            .Which.Writer.Should().Be(StatusWriter.PreviousBinaryLegacy);

        var world = new MixedBinaryWorld();

        // B3's terminal owns v8 (document carries the route, epoch 1).
        (await world.TerminalObserveAsync(Publication(8, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(8, carriedRoute: route);

        // Rollback to B2 (reachable only by rolling the binary back — the v2 gate opened because
        // every silo advertised v2). B2 has no writer for this route at any version: the document
        // freezes at B3's last write instead of being rewritten by a phase-unaware binary.
        (await world.PreviousBinaryTerminalWriteAsync(Publication(8, route))).Should().BeNull();
        (await world.PreviousBinaryTerminalWriteAsync(Publication(9, route))).Should().BeNull();
        (await world.PreviousBinaryLegacyWriteAsync(Publication(9, route))).Should().BeNull();
        await world.AssertStoreIsByteImageOfAsync(8, carriedRoute: route);
        world.Journal.Writes.Should().ContainSingle("only B3's terminal ever reached the store");
        await world.AssertExactlyOneStoredDocumentAsync();

        // A binary from before routes existed (B0) still writes unconditionally; there the epoch
        // fence, not the route gate, protects the document: a higher version wins, the same
        // version under a higher epoch is stale.
        (await world.OldBinaryWriteAsync(Publication(9, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(9, carriedRoute: null);
        (await world.OldBinaryWriteAsync(Publication(8, route))).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(Publication(9, route), WriteEvaluator.OldBinaryPlain))
            .Should().Be(ProjectionWriteDisposition.Duplicate, "B0 rewrote its own bytes");

        // Roll forward again: B3's terminal takes v9 over (epoch 1 > 0) and continues.
        (await world.TerminalObserveAsync(Publication(9, route))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(Publication(10, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(10, carriedRoute: route);

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
    }

    // ── 4. rolling 8d47b5e5c (B1) <-> B3 on a v1 route ─────────────────────────────────

    [Fact]
    public async Task RollingB1AndB3OnV1Route_AreDuplicatesAtEveryVersion_AndTheInPlaceContractUpgradeTakesOver()
    {
        var route = V1TerminalRoute(epoch: 1);
        var world = new MixedBinaryWorld();

        // B1's terminal wrote v1..v3 (route carried, epoch 1).
        for (long version = 1; version <= 3; version++)
            (await world.PreviousBinaryTerminalWriteAsync(Publication(version, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(3, carriedRoute: route);

        // B3's terminal re-observes v3 and continues; B1 stragglers at already covered versions
        // are duplicates under both evaluators (identical bytes), lower versions are stale.
        (await world.TerminalObserveAsync(Publication(3, route))).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.TerminalObserveAsync(Publication(4, route))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(4, route))).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(4, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(3, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Stale);

        // B1 reaches v5 first (rolling window): applied; B3's v5 is then the duplicate.
        (await world.PreviousBinaryTerminalWriteAsync(Publication(5, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(Publication(5, route))).Should().Be(ProjectionWriteDisposition.Duplicate);
        await world.AssertStoreIsByteImageOfAsync(5, carriedRoute: route);

        // In-place contract upgrade under B3 (ProjectionScopeGAgentBase.StatusRoute.cs
        // MaintainActiveTerminalRouteAsync -> UpgradeTerminalRouteContractAsync): the writer is
        // unchanged, the route moves to the CURRENT contract at a strictly higher epoch. Driven
        // through the real applier so the epoch/phase rules are production's.
        var upgraded = UpgradeContractInPlace(route, flipVersion: 6);
        upgraded.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        upgraded.ContractVersion.Should().Be(ProjectionScopeStatusGAgent.ContractVersion);
        upgraded.RouteEpoch.Should().Be(2);
        upgraded.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                upgraded,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue("the same materializer keeps the route across the upgrade");
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(upgraded)
            .Should().BeFalse("B1's gate never matches the upgraded contract");

        // B3 keeps writing under epoch 2; B1 has no route it may write for any more.
        (await world.TerminalObserveAsync(Publication(6, upgraded))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(6, upgraded))).Should().BeNull();
        (await world.PreviousBinaryTerminalWriteAsync(Publication(7, upgraded))).Should().BeNull();
        (await world.TerminalObserveAsync(Publication(7, upgraded))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(7, carriedRoute: upgraded);

        // A B1 write that still carries the pre-upgrade route at the same version (its last
        // in-flight publication) is fenced as Stale under the current evaluator — never a
        // conflict; only B1's own plain evaluator logs Conflict, and its store write is refused
        // either way. The reverse order is the takeover the upgrade epoch buys.
        var stored = (await world.Journal.Store.GetAsync(SourceScopeActorId))!;
        var lateB1 = await WriteThroughPreviousBinaryTerminalAsync(BuildSourceState(7, route), 7, new FixedProjectionClock(Now));
        ProjectionWriteResultEvaluator.Evaluate(stored, lateB1).Disposition.Should().Be(ProjectionWriteDisposition.Stale);
        PreviousBinaryProjectionWriteResultEvaluator.Evaluate(stored, lateB1).Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
        ProjectionWriteResultEvaluator.Evaluate(lateB1, stored).Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(7, route))).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(7, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Conflict);
        await world.AssertStoreIsByteImageOfAsync(7, carriedRoute: upgraded);

        (await world.PreviousBinaryTerminalWriteAsync(Publication(5, route))).Should().Be(ProjectionWriteDisposition.Stale, "a B1 straggler of a lower version");

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        await world.AssertStoreIsByteImageOfAsync(7, carriedRoute: upgraded);
        world.Journal.Writes.Select(static w => (w.Writer, w.Evaluator, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore, 1, ProjectionWriteDisposition.Applied),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore, 2, ProjectionWriteDisposition.Applied),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore, 3, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 3, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 4, ProjectionWriteDisposition.Applied),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore, 4, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.OldBinaryPlain, 4, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.OldBinaryPlain, 3, ProjectionWriteDisposition.Stale),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.OldBinaryPlain, 5, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 6, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore, 7, ProjectionWriteDisposition.Stale),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.OldBinaryPlain, 7, ProjectionWriteDisposition.Conflict),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Stale));
    }

    // ── 4b. old-binary terminal on an old silo during a B3 WARMING cutover ──────────────

    [Fact]
    public async Task PersistedWarmingRoute_AfterBridgeQuiescence_RejectsDelayedOldTerminalWrites()
    {
        // This route was persisted before Phase A and resumes only after the bridge is quiesced.
        // A delayed B1/B2-shaped terminal delivery still matches only v1, so the legacy shadow is
        // the sole WARMING writer and the current terminal is the sole BLOCKED writer.
        var world = new MixedBinaryWorld(terminalBridgeQuiesced: true);
        var routeCarrier = new ProjectionScopeState();
        var warming = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Warming);
        warming.WarmStartedVersion = 4;
        warming.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        AddPhaseBSeals(warming);
        var warmingStarted = new ProjectionScopeStatusRouteWarmingStartedEvent { Route = warming, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingStarted(routeCarrier, warmingStarted);
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(routeCarrier.StatusRoute)
            .Should().BeFalse("the v2 contract closes the phase-unaware gate, WARMING included");
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(routeCarrier.StatusRoute).Should().BeTrue();

        var v4 = Publication(4, routeCarrier.StatusRoute, eventData: warmingStarted);
        (await world.NewBinaryLegacyWriteAsync(v4)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(v4)).Should().BeNull("the old terminal's gate refuses a v2 route");
        (await world.TerminalObserveAsync(v4)).Should().BeNull("the B3 terminal reports while warming");
        world.Terminal.CaughtUpReports.Select(static r => r.ObservedVersion).Should().Equal(4);

        // What changed is the route CONTRACT, not the wire shape: the document the B3 legacy
        // shadow stored is byte-identical to what B1's mapper produces for the same publication.
        var storedWhileWarming = (await world.Journal.Store.GetAsync(SourceScopeActorId))!;
        storedWhileWarming.ToByteArray().Should().Equal(
            MapThroughPreviousBinary(v4).ToByteArray(),
            "B1's mapper and B3's mapper are the same code; only the gate differs");

        var caughtUp = new ProjectionScopeStatusRouteCaughtUpEvent { RouteEpoch = 1, ObservedVersion = 4, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteCaughtUp(routeCarrier, caughtUp);
        var v5 = Publication(5, routeCarrier.StatusRoute, eventData: caughtUp);
        (await world.PreviousBinaryTerminalWriteAsync(v5, WriteEvaluator.OldBinaryPlain)).Should().BeNull();
        (await world.NewBinaryLegacyWriteAsync(v5)).Should().Be(ProjectionWriteDisposition.Applied, "the legacy shadow is the only warming writer");
        await world.AssertStoreIsByteImageOfAsync(5, carriedRoute: routeCarrier.StatusRoute);

        // BLOCKED: the B3 legacy shadow stops; the B3 terminal writes; the old terminal still
        // matches nothing, so no second writer appears at the takeover version either.
        var blocked = new ProjectionScopeStatusRouteBlockedEvent { RouteEpoch = 1, BlockedVersion = 6, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteBlocked(routeCarrier, blocked);
        routeCarrier.StatusRoute!.BlockedVersion.Should().Be(6);
        var v6 = Publication(6, routeCarrier.StatusRoute, eventData: blocked);
        (await world.NewBinaryLegacyWriteAsync(v6)).Should().BeNull();
        (await world.TerminalObserveAsync(v6)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(v6)).Should().BeNull();
        (await world.PreviousBinaryTerminalWriteAsync(v6, WriteEvaluator.OldBinaryPlain)).Should().BeNull();

        world.AssertNoConflictFromTheCurrentStore();
        world.Journal.Writes.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Conflict);
        world.Journal.Writes.Should().NotContain(static w => w.Writer == StatusWriter.PreviousBinaryTerminal);
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        await world.AssertStoreIsByteImageOfAsync(6, carriedRoute: routeCarrier.StatusRoute);
    }

    // ── 5. rolling forward from B0 through the phased cutover ──────────────────────────

    [Fact]
    public async Task PersistedForwardCutover_AfterBridgeQuiescence_NeverConflicts()
    {
        var world = new MixedBinaryWorld(terminalBridgeQuiesced: true);

        // B0's legacy shadow owns v1..v3 (no route on the source).
        for (long version = 1; version <= 3; version++)
            (await world.OldBinaryWriteAsync(Publication(version, route: null))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.OldBinaryWriteAsync(Publication(3, route: null))).Should().Be(ProjectionWriteDisposition.Duplicate, "a B0 redelivery of the current version");
        await world.AssertStoreIsByteImageOfAsync(3, carriedRoute: null);

        // A cutover persisted before Phase A resumes after bridge quiescence. Phase A itself never
        // creates this route. Its WarmingStarted commit is v4, so WarmStartedVersion = 4; every
        // subsequent protocol commit is carried through the real appliers.
        var routeCarrier = new ProjectionScopeState();
        var warming = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Warming);
        warming.WarmStartedVersion = 4;
        warming.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        AddPhaseBSeals(warming);
        var warmingStarted = new ProjectionScopeStatusRouteWarmingStartedEvent { Route = warming, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingStarted(routeCarrier, warmingStarted);
        routeCarrier.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        var v4 = Publication(4, routeCarrier.StatusRoute, eventData: warmingStarted);

        // v4 WARMING: the legacy shadow (B3 projector) still writes, the terminal only reports.
        // The B0 straggler delivers v4 first (epoch 0): applied, then taken over by the B3 legacy
        // write at epoch 1 — two legacy binaries at one version resolved by the fence.
        (await world.OldBinaryWriteAsync(v4)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(v4)).Should().Be(ProjectionWriteDisposition.Applied, "epoch-1 takeover of the epoch-0 document");
        (await world.TerminalObserveAsync(v4)).Should().BeNull("a warming terminal writes nothing");
        world.Terminal.Agent.State.Active.Should().BeTrue("the warming route names the terminal, which starts");
        world.Terminal.CaughtUpReports.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { SourceScopeActorId, RouteEpoch = 1L, ObservedVersion = 4L });
        await world.AssertStoreIsByteImageOfAsync(4, carriedRoute: routeCarrier.StatusRoute);

        // A restarted source first commits its post-quiescence WARMING probe (v5). Only a terminal
        // report that has observed that durable fence can advance the route.
        var warmingProbed = new ProjectionScopeStatusRouteWarmingProbedEvent
        {
            RouteEpoch = 1,
            RequiredObservedVersion = 5,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingProbed(routeCarrier, warmingProbed);
        routeCarrier.StatusRoute!.WarmingProbeVersion.Should().Be(5);
        routeCarrier.StatusRoute.CaughtUpVersion.Should().Be(0);
        var v5 = Publication(5, routeCarrier.StatusRoute, eventData: warmingProbed);
        (await world.NewBinaryLegacyWriteAsync(v5)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v5)).Should().BeNull();
        world.Terminal.CaughtUpReports.Select(static r => r.ObservedVersion).Should().Equal(4, 5);
        (await world.OldBinaryWriteAsync(v5)).Should().Be(ProjectionWriteDisposition.Stale);

        // The exact report produces CaughtUp (v6), then BLOCKED (v7). The legacy shadow remains
        // the writer through CaughtUp; the terminal takes over only at BLOCKED.
        var caughtUp = new ProjectionScopeStatusRouteCaughtUpEvent
        {
            RouteEpoch = 1,
            ObservedVersion = 5,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteCaughtUp(routeCarrier, caughtUp);
        routeCarrier.StatusRoute!.CaughtUpVersion.Should().Be(5);
        var v6 = Publication(6, routeCarrier.StatusRoute, eventData: caughtUp);
        (await world.NewBinaryLegacyWriteAsync(v6)).Should().Be(ProjectionWriteDisposition.Applied, "still warming: the legacy shadow writes");
        (await world.TerminalObserveAsync(v6)).Should().BeNull();
        world.Terminal.CaughtUpReports.Select(static r => r.ObservedVersion).Should().Equal(4, 5, 6);

        var blocked = new ProjectionScopeStatusRouteBlockedEvent
        {
            RouteEpoch = 1,
            BlockedVersion = 7,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteBlocked(routeCarrier, blocked);
        routeCarrier.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        routeCarrier.StatusRoute.BlockedVersion.Should().Be(7);
        var v7 = Publication(7, routeCarrier.StatusRoute, eventData: blocked);
        // v7 BLOCKED: the B3 legacy shadow skips; the terminal writes (BLOCKED is a writing phase:
        // the epoch-fenced takeover version). B0 never saw the block and delivers v7 too: before
        // the terminal it is applied then taken over, after it
        // it is stale — never a conflict.
        (await world.NewBinaryLegacyWriteAsync(v7)).Should().BeNull();
        (await world.OldBinaryWriteAsync(v7)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v7)).Should().Be(ProjectionWriteDisposition.Applied, "epoch-1 takeover of B0's epoch-0 v7");
        (await world.OldBinaryWriteAsync(v7)).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(v7, WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Conflict, "B0's own log only");
        await world.AssertStoreIsByteImageOfAsync(7, carriedRoute: routeCarrier.StatusRoute);
        world.Terminal.CaughtUpReports.Should().HaveCount(3, "no report once the route left WARMING");

        // The source commits a dedicated drain probe (v8). The previous writer's typed release
        // confirmation must name that exact route and cover the committed probe before the source
        // records release (v9) and ACTIVE (v10).
        var drainProbed = new ProjectionScopeStatusRouteDrainProbedEvent
        {
            RouteEpoch = 1,
            RequiredObservedVersion = 8,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteDrainProbed(routeCarrier, drainProbed);
        routeCarrier.StatusRoute!.DrainProbeVersion.Should().Be(8);
        var v8 = Publication(8, routeCarrier.StatusRoute, eventData: drainProbed);
        (await world.NewBinaryLegacyWriteAsync(v8)).Should().BeNull();
        (await world.TerminalObserveAsync(v8)).Should().Be(ProjectionWriteDisposition.Applied);

        var releaseConfirmation = new ProjectionScopeStatusWriterReleasedEvent
        {
            SourceScopeActorId = SourceScopeActorId,
            WriterActorId = LegacyActorId,
            RouteEpoch = 1,
            LastObservedVersion = 8,
            ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        releaseConfirmation.RouteEpoch.Should().Be(routeCarrier.StatusRoute.RouteEpoch);
        releaseConfirmation.LastObservedVersion.Should().BeGreaterThanOrEqualTo(
            routeCarrier.StatusRoute.DrainProbeVersion);

        var released = new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = releaseConfirmation.RouteEpoch,
            ReleasedWriterObservedVersion = releaseConfirmation.LastObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(routeCarrier, released);
        routeCarrier.StatusRoute!.LegacyRouteReleased.Should().BeTrue();
        var v9 = Publication(9, routeCarrier.StatusRoute, eventData: released);
        (await world.TerminalObserveAsync(v9)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(v9)).Should().BeNull();

        var flipped = routeCarrier.StatusRoute.Clone();
        flipped.Phase = ProjectionScopeStatusRoutePhase.Active;
        flipped.FlipVersion = 10;
        flipped.LegacyRouteReleased = true;
        var activated = new ProjectionScopeStatusRouteActivatedEvent { Route = flipped, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteActivated(routeCarrier, activated);
        routeCarrier.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        var v10 = Publication(10, routeCarrier.StatusRoute, eventData: activated);
        (await world.TerminalObserveAsync(v10)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(v10)).Should().BeNull();
        (await world.TerminalObserveAsync(Publication(11, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Applied);

        // Delayed B0 deliveries after the flip: lower versions and the already-taken-over versions.
        (await world.OldBinaryWriteAsync(Publication(3, route: null))).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(v10)).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(Publication(11, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Stale);

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        await world.AssertStoreIsByteImageOfAsync(11, carriedRoute: routeCarrier.StatusRoute);
        var final = (await world.Journal.Store.GetAsync(SourceScopeActorId))!;
        final.StatusRoute.RouteEpoch.Should().Be(1);
        final.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        world.Journal.Writes.Select(static w => (w.Writer, w.Evaluator, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 1, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 2, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 3, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 3, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 4, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, WriteEvaluator.CurrentStore, 4, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Stale),
            (StatusWriter.NewBinaryLegacy, WriteEvaluator.CurrentStore, 6, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 7, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, WriteEvaluator.OldBinaryPlain, 7, ProjectionWriteDisposition.Conflict),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 8, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 9, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 10, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 11, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 3, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 10, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 11, ProjectionWriteDisposition.Stale));
    }

    // ── 6. rollback on revocation through the phases ───────────────────────────────────

    [Fact]
    public async Task PersistedRollbackCutover_AfterBridgeQuiescence_NeverConflicts()
    {
        var world = new MixedBinaryWorld(terminalBridgeQuiesced: true);

        // Terminal route ACTIVE at epoch 1 (flipped earlier); the terminal owns v8.
        var routeCarrier = new ProjectionScopeState();
        var active = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Active);
        active.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        active.FlipVersion = 8;
        active.LegacyRouteReleased = true;
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteActivated(
            routeCarrier,
            new ProjectionScopeStatusRouteActivatedEvent { Route = active, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) });
        (await world.TerminalObserveAsync(Publication(8, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(Publication(8, routeCarrier.StatusRoute))).Should().BeNull();

        // A rollback cutover persisted before Phase A resumes only after bridge quiescence. Phase A
        // does not create this legacy route from revocation; the historical WARMING commit is v9.
        var legacyWarming = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, ProjectionScopeStatusRoutePhase.Warming);
        legacyWarming.WarmStartedVersion = 9;
        legacyWarming.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        AddPhaseBSeals(legacyWarming);
        var warmingStarted = new ProjectionScopeStatusRouteWarmingStartedEvent { Route = legacyWarming, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingStarted(routeCarrier, warmingStarted);
        ProjectionScopeStatusRoutePolicy.IsLegacyRoute(routeCarrier.StatusRoute).Should().BeTrue();
        routeCarrier.StatusRoute!.RouteEpoch.Should().Be(2);
        var v9 = Publication(9, routeCarrier.StatusRoute, eventData: warmingStarted);

        // v9 legacy WARMING: the terminal keeps writing (under epoch 2 now); the B3 legacy
        // projector does not write yet.
        (await world.TerminalObserveAsync(v9)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(v9)).Should().BeNull();
        world.Terminal.Agent.State.Released.Should().BeFalse();
        await world.AssertStoreIsByteImageOfAsync(9, carriedRoute: routeCarrier.StatusRoute);

        // The restarted source commits the receipt-gated WARMING fence at v10. A hosted legacy
        // candidate reports that exact observed version before the source records CaughtUp at v11.
        var warmingProbed = new ProjectionScopeStatusRouteWarmingProbedEvent
        {
            RouteEpoch = 2,
            RequiredObservedVersion = 10,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingProbed(
            routeCarrier,
            warmingProbed);
        routeCarrier.StatusRoute!.WarmingProbeVersion.Should().Be(10);
        routeCarrier.StatusRoute.CaughtUpVersion.Should().Be(0);
        var v10 = Publication(10, routeCarrier.StatusRoute, eventData: warmingProbed);
        (await world.TerminalObserveAsync(v10)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(v10)).Should().BeNull();

        var caughtUpReport = new ProjectionScopeStatusWriterCaughtUpEvent
        {
            SourceScopeActorId = SourceScopeActorId,
            WriterActorId = LegacyActorId,
            RouteEpoch = 2,
            ObservedVersion = 10,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        caughtUpReport.ObservedVersion.Should().BeGreaterThanOrEqualTo(
            routeCarrier.StatusRoute.WarmingProbeVersion);
        var caughtUp = new ProjectionScopeStatusRouteCaughtUpEvent
        {
            RouteEpoch = caughtUpReport.RouteEpoch,
            ObservedVersion = caughtUpReport.ObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteCaughtUp(routeCarrier, caughtUp);
        routeCarrier.StatusRoute!.CaughtUpVersion.Should().Be(10);
        var v11 = Publication(11, routeCarrier.StatusRoute, eventData: caughtUp);
        (await world.TerminalObserveAsync(v11)).Should().Be(
            ProjectionWriteDisposition.Applied,
            "the terminal remains the writer until the rollback route blocks");
        (await world.NewBinaryLegacyWriteAsync(v11)).Should().BeNull();

        // v12 legacy BLOCKED: the legacy shadow takes over. This recovery fixture restores the
        // previous terminal relay after the BLOCKED commit, so the terminal's first drain
        // publication is the dedicated v13 probe below rather than an unfenced release.
        var blocked = new ProjectionScopeStatusRouteBlockedEvent
        {
            RouteEpoch = 2,
            BlockedVersion = 12,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteBlocked(routeCarrier, blocked);
        routeCarrier.StatusRoute!.BlockedVersion.Should().Be(12);
        var v12 = Publication(12, routeCarrier.StatusRoute, eventData: blocked);
        (await world.NewBinaryLegacyWriteAsync(v12)).Should().Be(ProjectionWriteDisposition.Applied);
        world.Terminal.Agent.State.Released.Should().BeFalse();

        var drainProbed = new ProjectionScopeStatusRouteDrainProbedEvent
        {
            RouteEpoch = 2,
            RequiredObservedVersion = 13,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteDrainProbed(
            routeCarrier,
            drainProbed);
        routeCarrier.StatusRoute!.DrainProbeVersion.Should().Be(13);
        var v13 = Publication(13, routeCarrier.StatusRoute, eventData: drainProbed);
        (await world.NewBinaryLegacyWriteAsync(v13)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v13)).Should().BeNull(
            "the previous terminal drains and confirms without writing the legacy route");
        world.Terminal.Agent.State.Released.Should().BeTrue();
        world.Terminal.Agent.State.ReleasedAtObservedVersion.Should().Be(13);
        world.Terminal.EventSourcing.Committed
            .OfType<ProjectionScopeStatusTerminalReleasedEvent>()
            .Should().ContainSingle().Which.LastObservedVersion.Should().Be(13);
        var releaseConfirmation = world.Terminal.Outbox.Sent
            .Where(static sent => sent.TargetActorId == SourceScopeActorId)
            .Select(static sent => sent.Message)
            .OfType<ProjectionScopeStatusWriterReleasedEvent>()
            .Should().ContainSingle().Subject;
        releaseConfirmation.Should().BeEquivalentTo(new ProjectionScopeStatusWriterReleasedEvent
        {
            SourceScopeActorId = SourceScopeActorId,
            WriterActorId = TerminalActorId,
            RouteEpoch = 2,
            LastObservedVersion = 13,
            ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
        });
        releaseConfirmation.LastObservedVersion.Should().BeGreaterThanOrEqualTo(
            routeCarrier.StatusRoute.DrainProbeVersion);
        await world.AssertStoreIsByteImageOfAsync(13, carriedRoute: routeCarrier.StatusRoute);

        // The source accepts the exact previous writer's durable confirmation, records its drain
        // watermark at v14, removes that relay, and flips the legacy route ACTIVE at v15.
        var released = new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = releaseConfirmation.RouteEpoch,
            ReleasedWriterObservedVersion = releaseConfirmation.LastObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        released.ReleasedWriterObservedVersion.Should().Be(13);
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(routeCarrier, released);
        routeCarrier.StatusRoute!.LegacyRouteReleased.Should().BeTrue();
        var v14 = Publication(14, routeCarrier.StatusRoute, eventData: released);
        (await world.NewBinaryLegacyWriteAsync(v14)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v14)).Should().BeNull("released, and a legacy route never names the terminal");

        var flipped = routeCarrier.StatusRoute!.Clone();
        flipped.Phase = ProjectionScopeStatusRoutePhase.Active;
        flipped.FlipVersion = 15;
        flipped.LegacyRouteReleased = true;
        var activated = new ProjectionScopeStatusRouteActivatedEvent { Route = flipped, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteActivated(routeCarrier, activated);
        routeCarrier.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        var v15 = Publication(15, routeCarrier.StatusRoute, eventData: activated);
        (await world.NewBinaryLegacyWriteAsync(v15)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v15)).Should().BeNull();
        world.Terminal.Agent.State.Released.Should().BeTrue("a legacy route in a writing phase never restarts the terminal");

        // Late redeliveries while draining: the released terminal ignores its old versions; the
        // legacy shadow's redelivery of the current version is a duplicate and of an older one
        // stale; a late delivery of a warming-phase version is skipped by the legacy projector; a
        // B0 old binary (if a rollback went all the way back) takes the document forward with a
        // higher version.
        (await world.TerminalObserveAsync(v11)).Should().BeNull();
        (await world.NewBinaryLegacyWriteAsync(v15)).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.NewBinaryLegacyWriteAsync(v12)).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.NewBinaryLegacyWriteAsync(v9)).Should().BeNull();
        (await world.OldBinaryWriteAsync(Publication(15, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(Publication(16, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Applied, "version wins over epoch");

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.CaughtUpReports.Should().BeEmpty("the terminal reports only while a TERMINAL route warms");
        await world.AssertStoreIsByteImageOfAsync(16, carriedRoute: null);
        world.Journal.Writes.Select(static w => (w.Writer, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.Terminal, 8, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 9, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 10, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 11, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 12, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 13, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 14, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 15, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 15, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.NewBinaryLegacy, 12, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, 15, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, 16, ProjectionWriteDisposition.Applied));
    }

    // ── 7. sanity: byte equality is not relaxed inside an epoch ────────────────────────

    [Theory]
    [InlineData(StatusWriter.PreviousBinaryTerminal)]
    [InlineData(StatusWriter.Terminal)]
    public async Task SameEpochSameVersionDifferentEventIds_FromB1AndB3_IsStillAConflict(StatusWriter firstWriter)
    {
        const long version = 3;
        var route = V1TerminalRoute(epoch: 1); // both binaries write under epoch 1
        var world = new MixedBinaryWorld();

        if (firstWriter == StatusWriter.PreviousBinaryTerminal)
        {
            (await world.PreviousBinaryTerminalWriteAsync(Publication(version, route, "source-evt-3-from-b1")))
                .Should().Be(ProjectionWriteDisposition.Applied);

            // A rejected OBSERVED write never advances delivery and persists nothing: the
            // observation fails so the provider redelivers it without moving its checkpoint.
            var rejected = await world.TerminalObserveRejectedAsync(Publication(version, route, "source-evt-3-different-fact"));
            rejected.Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
            rejected.MaterializerActorId.Should().Be(TerminalActorId);
            rejected.Source.StateVersion.Should().Be(version);
            rejected.Source.EventId.Should().Be("source-evt-3-different-fact");
            world.Terminal.Agent.State.PendingWrite.Should().BeNull("nothing about a rejected observation is persisted");
            world.Terminal.EventSourcing.Committed.OfType<ProjectionScopeStatusWriteDeferredEvent>().Should().BeEmpty();
            await world.AssertStoreIsByteImageOfAsync(version, carriedRoute: route, eventId: "source-evt-3-from-b1");
        }
        else
        {
            (await world.TerminalObserveAsync(Publication(version, route, "source-evt-3-from-b3")))
                .Should().Be(ProjectionWriteDisposition.Applied);
            (await world.PreviousBinaryTerminalWriteAsync(Publication(version, route, "source-evt-3-different-fact")))
                .Should().Be(ProjectionWriteDisposition.Conflict);
            (await world.PreviousBinaryTerminalWriteAsync(Publication(version, route, "source-evt-3-different-fact"), WriteEvaluator.OldBinaryPlain))
                .Should().Be(ProjectionWriteDisposition.Conflict);
            world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        }

        world.Journal.Writes.Should().Contain(static w => w.Disposition == ProjectionWriteDisposition.Conflict);
        world.Journal.Writes.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Gap);
        world.Journal.Writes.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Duplicate);
    }

    [Fact]
    public async Task EpochZeroSameVersionDifferentEventIds_FromB0AndTheNewLegacyShadow_IsStillAConflict()
    {
        const long version = 3;
        var world = new MixedBinaryWorld();

        (await world.OldBinaryWriteAsync(Publication(version, route: null, "source-evt-3-from-b0"))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(Publication(version, route: null, "source-evt-3-different-fact")))
            .Should().Be(ProjectionWriteDisposition.Conflict, "both documents carry epoch 0: the plain identity rules apply");
        await world.AssertStoreIsByteImageOfAsync(version, carriedRoute: null, eventId: "source-evt-3-from-b0");
    }

    [Fact]
    public async Task SameEpochSameVersionSameEventIdDifferentStateBytes_IsStillAConflict()
    {
        const long version = 3;
        var route = V1TerminalRoute(epoch: 1);
        var world = new MixedBinaryWorld();

        (await world.PreviousBinaryTerminalWriteAsync(Publication(version, route))).Should().Be(ProjectionWriteDisposition.Applied);

        var divergedState = BuildSourceState(version, route);
        divergedState.LastSuccessfulVersion += 1;
        var rejected = await world.TerminalObserveRejectedAsync(Publication(version, divergedState));
        rejected.Disposition.Should().Be(
            ProjectionWriteDisposition.Conflict,
            "same version, event id and epoch but different bytes is a different fact");

        world.Terminal.Agent.State.PendingWrite.Should().BeNull("a rejected observation persists nothing and is redelivered");
        await world.AssertStoreIsByteImageOfAsync(version, carriedRoute: route);
    }

    // ── 8. mixed-binary SOURCE activation (the P1 the contract revision closes) ─────────

    /// <summary>
    /// (a) The counterexample. An old, phase-unaware SOURCE binary admitted through the v1 gate
    /// treats ANY route it matches as ACTIVE — it cannot see the phase — so on activation it
    /// re-asserts the terminal relay and releases the legacy shadow while the current binary has
    /// the route only WARMING (the shadow is the writer) or BLOCKED (the cutover is waiting for
    /// the previous writer's typed release confirmation). Its legacy shadow scope releases itself
    /// on the very first routed publication as well. Both rules verbatim from 416e80f4a.
    /// (b) What the revision actually removes, and what it does not. The route contract closes the
    /// old source's "route already active" branch for every route this binary commits — that
    /// branch, and only that branch. The same activation record still adopts a FRESH v1 route at a
    /// strictly higher epoch whenever a v1 grant is open (closed by the capability revision, not by
    /// the route contract), and the old legacy shadow still releases itself on every one of those
    /// routes, WARMING included. Both residuals are asserted below against the current appliers and
    /// policy; the freeze they cause is
    /// <see cref="MixedFleetDuringPersistedV2Warming_FreezesUntilBridgeQuiescesThenRecovers"/>.
    /// </summary>
    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    public async Task OldBinarySourceOnAPhasedPreviousContractRoute_WouldStealTheCutover_TheRevisionClosesItsActiveBranchOnly(
        ProjectionScopeStatusRoutePhase phase)
    {
        // The route shape a phased cutover committed BEFORE the revision: previous contract,
        // reader version 1, carrying a phase the old binary cannot decode.
        var route = PhasedPreviousContractRoute(epoch: 1, phase);
        var warming = phase == ProjectionScopeStatusRoutePhase.Warming;

        // The current binary's writers for that route.
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(route)
            .Should().Be(warming, "the legacy shadow is the writer while the route warms, and only then");
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().Be(warming, "a warming terminal only reports its observed version");
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().Be(!warming, "BLOCKED is the writing phase in which the terminal takes the document over");
        route.LegacyRouteReleased.Should().BeFalse("the cutover has not been confirmed by the previous writer yet");

        // The old SOURCE binary takes its "route already active" branch in BOTH phases.
        var activation = PreviousBinarySourceScopeStatusRouteRules.OnActivation(route, freshV1Grant: true);
        activation.TreatsRouteAsActive.Should().BeTrue("its gate is contract id + exact reader version and knows no phase");
        activation.ReassertsTerminalRelay.Should().BeTrue();
        activation.ReleasesLegacyShadow.Should().BeTrue(
            warming
                ? "it would release the only writer the warming route has"
                : "it would release the previous writer without ever collecting its typed confirmation");
        activation.PersistsLegacyRouteReleasedForEpoch.Should().Be(1);
        activation.AdoptsFreshTerminalRouteAtEpoch.Should().BeNull("the active branch returns before the adoption path");

        // Its legacy shadow scope releases itself on the first publication carrying ANY route.
        PreviousBinarySourceScopeStatusRouteRules.LegacyShadowReleasesItselfOnObserving(route).Should().BeTrue();

        // The damage, spelled out against the CURRENT appliers: the release fact the old binary
        // persists is accepted at the same epoch, so the current source would record the previous
        // writer as released — and flip out of BLOCKED — without ever having seen the writer
        // confirm that it drained (ProjectionScopeStatusWriterReleasedEvent).
        var stolen = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(
            new ProjectionScopeState { StatusRoute = route.Clone() },
            new ProjectionScopeStatusLegacyRouteReleasedEvent
            {
                RouteEpoch = route.RouteEpoch,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
        stolen.StatusRoute!.LegacyRouteReleased.Should().BeTrue();
        stolen.StatusRoute.Phase.Should().Be(phase, "the phase is untouched: only the release fact was forged");

        // (b, route level) The revision removes ONE branch at its root: every route this binary can
        // commit for a cutover — terminal in any phase, or the legacy rollback route — is one the
        // old source's ACTIVE gate does not match, and one its terminal writer refuses. The rest of
        // the old binary's behaviour on those very routes is unchanged, so it is asserted here in
        // full instead of being narrowed to the one field the revision moves.
        foreach (var committed in AllPhases.Select(p => ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, p))
                     .Concat(AllPhases.Select(p => ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, p))))
        {
            committed.ContractId.Should().NotBe(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
            var onCommitted = PreviousBinarySourceScopeStatusRouteRules.OnActivation(committed, freshV1Grant: true);
            onCommitted.TreatsRouteAsActive.Should().BeFalse("the v1 activation gate matches no route this binary commits");
            PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(committed).Should().BeFalse();

            // RESIDUAL 1 (source): with an open v1 grant the SAME activation falls through to the
            // fresh-adoption branch and builds a v1 route at the next epoch — releasing the legacy
            // shadow and re-asserting the terminal relay on the way. The route contract does not
            // close this; only the closed v1 capability gate does.
            onCommitted.AdoptsFreshTerminalRouteAtEpoch.Should().Be(committed.RouteEpoch + 1);
            onCommitted.PersistsLegacyRouteReleasedForEpoch.Should().Be(committed.RouteEpoch + 1);
            onCommitted.ReleasesLegacyShadow.Should().BeTrue();
            onCommitted.ReassertsTerminalRelay.Should().BeTrue();

            // And that fresh route is one the CURRENT binary would then serve and let win: it is a
            // v1 terminal route (an earlier contract, so IsActiveTerminalRoute accepts it) at a
            // strictly higher epoch, so ApplyStatusRouteActivated replaces the committed cutover
            // route wholesale — phase forced ACTIVE, LegacyRouteReleased reset.
            var forged = V1TerminalRoute(onCommitted.AdoptsFreshTerminalRouteAtEpoch!.Value);
            ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                    forged,
                    ProjectionScopeStatusGAgent.ContractId,
                    ProjectionScopeStatusGAgent.ContractVersion)
                .Should().BeTrue("a materializer of a later contract serves every earlier terminal route");
            var replaced = ProjectionScopeStateApplier.ApplyStatusRouteActivated(
                new ProjectionScopeState { StatusRoute = committed.Clone() },
                new ProjectionScopeStatusRouteActivatedEvent
                {
                    Route = forged,
                    OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
                });
            replaced.StatusRoute!.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
            replaced.StatusRoute.RouteEpoch.Should().Be(committed.RouteEpoch + 1);
            replaced.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
            replaced.StatusRoute.LegacyRouteReleased.Should().BeFalse("a direct activation at a higher epoch is not the flip of this cutover");

            // RESIDUAL 2 (shadow): 416e80f4a's shadow self-release is gated by its IsTerminalRoute
            // = ANY non-empty route, so it fires on every route this binary commits — the legacy
            // rollback route it is itself the writer for, and a WARMING terminal route the current
            // policy says it must keep writing. The revision does not reach this rule at all.
            PreviousBinarySourceScopeStatusRouteRules.LegacyShadowReleasesItselfOnObserving(committed)
                .Should().BeTrue("the old shadow releases itself on ANY committed route, not only on one that supersedes it");
            if (ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(committed))
            {
                ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(committed)
                    .Should().BeFalse("the current shadow releases itself only once a terminal route is in a writing phase");
            }
        }

        ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase).ContractId
            .Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2);
        ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase).ContractVersion
            .Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);

        // (c) The steal residual 1 buys, driven through the real writers while an active old member
        // keeps the bridge unquiesced. WARMING still has the current legacy writer; BLOCKED is
        // frozen. A v1 route an old source adopts under a fresh grant sits at epoch 2, so its old
        // terminal gate accepts it and either takes over or creates the v9 document. The closed v1
        // capability gate remains the defence.
        var world = new MixedBinaryWorld();
        var committedV2 = V2TerminalRoute(epoch: 1, phase);
        var v9 = Publication(9, committedV2);
        if (warming)
        {
            (await world.NewBinaryLegacyWriteAsync(v9)).Should().Be(
                ProjectionWriteDisposition.Applied,
                "the legacy shadow is the writer while the v2 route warms");
            (await world.TerminalObserveAsync(v9)).Should().BeNull("a warming terminal only reports");
        }
        else
        {
            (await world.NewBinaryLegacyWriteAsync(v9)).Should().BeNull("the shadow stops once the route blocks");
            (await world.TerminalObserveAsync(v9)).Should().BeNull(
                "the current terminal is silent while an active old member prevents bridge quiescence");
        }

        if (warming)
            await world.AssertStoreIsByteImageOfAsync(9, carriedRoute: committedV2);
        else
            (await world.Journal.Store.GetAsync(SourceScopeActorId)).Should().BeNull();
        var freshV1 = V1TerminalRoute(epoch: 2);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(9, freshV1)))
            .Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(9, carriedRoute: freshV1);
        await world.AssertExactlyOneStoredDocumentAsync();
        world.AssertNoConflictFromTheCurrentStore();
    }

    /// <summary>
    /// An active old member keeps the bridge unquiesced. Its shadow may release itself on the
    /// persisted V2 WARMING route, while every current writer stays silent, so the status document
    /// freezes without corruption. Exact bridge unanimity then supplies the receipt: the source
    /// restores the candidate, commits a fresh WARMING fence, and resumes the persisted cutover.
    /// </summary>
    [Fact]
    public async Task MixedFleetDuringPersistedV2Warming_FreezesUntilBridgeQuiescesThenRecovers()
    {
        var world = new MixedBinaryWorld();
        var routeCarrier = new ProjectionScopeState();
        var warming = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Warming);
        warming.WarmStartedVersion = 4;
        warming.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        var warmingStarted = new ProjectionScopeStatusRouteWarmingStartedEvent
        {
            Route = warming,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingStarted(routeCarrier, warmingStarted);
        var warmingRoute = routeCarrier.StatusRoute!;

        // While every silo runs B3 the shadow is the writer for this exact route, and the current
        // policy keeps it alive: a WARMING terminal route does not supersede it.
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(warmingRoute).Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(warmingRoute)
            .Should().BeFalse("the current shadow releases itself only once a terminal route is in a writing phase");
        (await world.NewBinaryLegacyWriteAsync(Publication(4, warmingRoute, eventData: warmingStarted)))
            .Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(4, carriedRoute: warmingRoute);

        // The downgrade mid-cutover. The shadow scope activates on a rolled-back silo, observes
        // this very route and releases itself; ReleaseScopeAsync is a durable scope fact, so it
        // stays gone for the rest of the cutover.
        PreviousBinarySourceScopeStatusRouteRules.LegacyShadowReleasesItselfOnObserving(warmingRoute)
            .Should().BeTrue("416e80f4a releases the shadow on ANY committed route, and a v2 WARMING route is one");
        PreviousBinaryRouteGatedLegacyProjector.WritesFor(warmingRoute)
            .Should().BeFalse("and while it drains, that binary's projector skips the routed source anyway");

        // v5 and v6 therefore reach NO writer at all — same route, same phase and same publication
        // shape as the v4 write above, so the only thing that changed is that the shadow is gone.
        foreach (var version in new long[] { 5, 6 })
        {
            var publication = Publication(version, warmingRoute);
            (await world.PreviousBinaryLegacyWriteAsync(publication)).Should().BeNull();
            (await world.PreviousBinaryTerminalWriteAsync(publication)).Should().BeNull();
            (await world.PreviousBinaryTerminalWriteAsync(publication, WriteEvaluator.OldBinaryPlain)).Should().BeNull();
            (await world.TerminalObserveAsync(publication)).Should().BeNull("a warming terminal reports, it never writes");
        }

        // FROZEN, not corrupt: not one write was attempted at 5 or 6, the stored document is still
        // the byte image of v4, and this source scope still has exactly one document.
        world.Journal.Writes.Should().ContainSingle("only the pre-downgrade shadow write ever reached the store");
        await world.AssertStoreIsByteImageOfAsync(4, carriedRoute: warmingRoute);
        await world.AssertExactlyOneStoredDocumentAsync();

        world.Terminal.CaughtUpReports.Should().BeEmpty(
            "writers cannot emit old-source-compatible continuations before bridge quiescence");

        // Once every active member advertises the bridge contract, the source re-creates the
        // candidate and commits one durable WARMING probe fence. The restored shadow writes that
        // current state and the terminal may finally report it.
        world.QuiesceTerminalBridge();
        AddPhaseBSeals(routeCarrier.StatusRoute!);
        var warmingProbe = new ProjectionScopeStatusRouteWarmingProbedEvent
        {
            RouteEpoch = 1,
            RequiredObservedVersion = 7,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingProbed(routeCarrier, warmingProbe);
        var v7 = Publication(7, routeCarrier.StatusRoute, eventData: warmingProbe);
        (await world.NewBinaryLegacyWriteAsync(v7)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(v7)).Should().BeNull();
        (await world.TerminalObserveAsync(v7)).Should().BeNull("a warming terminal reports without writing");
        world.Terminal.CaughtUpReports.Select(static report => report.ObservedVersion).Should().Equal(7);

        var caughtUp = new ProjectionScopeStatusRouteCaughtUpEvent
        {
            RouteEpoch = 1,
            ObservedVersion = 7,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteCaughtUp(routeCarrier, caughtUp);
        var v8 = Publication(8, routeCarrier.StatusRoute, eventData: caughtUp);
        (await world.NewBinaryLegacyWriteAsync(v8)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v8)).Should().BeNull();

        // The authenticated report allows the source to block at v9. The terminal takes the
        // restored legacy writer's document over under the same epoch fence, forward only.
        var blocked = new ProjectionScopeStatusRouteBlockedEvent
        {
            RouteEpoch = 1,
            BlockedVersion = 9,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteBlocked(routeCarrier, blocked);
        routeCarrier.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        var v9 = Publication(9, routeCarrier.StatusRoute, eventData: blocked);
        (await world.NewBinaryLegacyWriteAsync(v9)).Should().BeNull();
        (await world.PreviousBinaryTerminalWriteAsync(v9)).Should().BeNull();
        (await world.TerminalObserveAsync(v9)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(9, carriedRoute: routeCarrier.StatusRoute);
        await world.AssertExactlyOneStoredDocumentAsync();

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        // No mixed-fleet write occurred at v5/v6; repaired delivery resumes at the committed probe.
        world.Journal.Writes.Select(static w => (w.Writer, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.NewBinaryLegacy, 4, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 8, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 9, ProjectionWriteDisposition.Applied));
    }

    /// <summary>
    /// The Phase-A binary advertises the distinct quiescence bridge contract while the authority
    /// continues managing the historical V2 live gate. Therefore neither an old V2 member nor this
    /// binary can form V2 OPEN unanimity; only exact bridge unanimity can produce typed QUIESCED
    /// evidence. Authority transition behavior is covered by RuntimeFleetCapabilityAuthorityTests.
    /// </summary>
    [Fact]
    public void PhaseABinary_ClosesV2AdmissionAndMatchesOnlyTheQuiescencePolicy()
    {
        var current = new ProjectionScopeStatusTerminalCapabilityAdvertisement().GetCapability();
        current.Capability.Should().Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        current.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1);
        current.ReaderContractVersion.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion);

        var options = new Aevatar.Foundation.Core.Runtime.RuntimeFleetCapabilityAuthorityOptions();
        var liveRequirement = options.ManagedCapabilities
            .Where(static requirement =>
                requirement.Capability is RuntimeFleetCapability.ProjectionScopeStatusTerminalV1
                    or RuntimeFleetCapability.ProjectionScopeStatusTerminalV2)
            .Should().ContainSingle().Subject;
        liveRequirement.Capability.Should().Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        liveRequirement.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2);
        liveRequirement.MinimumReaderContractVersion.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersionV2);
        current.ContractId.Should().NotBe(liveRequirement.ContractId,
            "the Phase-A advertisement must close rather than refresh the historical V2 OPEN gate");

        var quiescencePolicy = options.QuiescencePolicies.Should().ContainSingle().Subject;
        quiescencePolicy.TargetCapability.Should().Be(current.Capability);
        quiescencePolicy.ContractId.Should().Be(current.ContractId);
        quiescencePolicy.MinimumReaderContractVersion.Should().Be(current.ReaderContractVersion);

        var previousV2 = new RuntimeFleetMemberCapability
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2,
            ReaderContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersionV2,
        };
        previousV2.Capability.Should().Be(current.Capability);
        previousV2.ContractId.Should().NotBe(current.ContractId,
            "reader-2 and bridge reader-3 members cannot form exact unanimity");

        var previous = PreviousBinaryAdvertisedCapability();
        previous.Capability.Should().Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV1);
        previous.Capability.Should().NotBe(current.Capability);
        previous.ContractId.Should().NotBe(current.ContractId);

        // The consequence for the OLD source binary of having no v1 admission: its adoption path
        // commits nothing — it keeps its current writer and never creates a route the phased
        // binary would have to share. The second call is the window that stays open for as long as
        // any v1 gate is: it commits a v1 terminal route and flips it ACTIVE in one step, with no
        // warming, no blocked phase and no drained previous writer.
        var withoutGrant = PreviousBinarySourceScopeStatusRouteRules.OnActivation(route: null, freshV1Grant: false);
        withoutGrant.AdoptsFreshTerminalRouteAtEpoch.Should().BeNull();
        withoutGrant.ReleasesLegacyShadow.Should().BeFalse();
        withoutGrant.ReassertsTerminalRelay.Should().BeFalse();
        PreviousBinarySourceScopeStatusRouteRules.OnActivation(route: null, freshV1Grant: true)
            .AdoptsFreshTerminalRouteAtEpoch.Should().Be(1);
    }

    /// <summary>
    /// (c) A v1 route an old binary created keeps its writer across the upgrade: the current
    /// terminal materializer serves it, and the current binary creates no v1 route of its own.
    /// (d) On that shared route the two binaries' same-version writes are byte-identical
    /// duplicates, never a silent Conflict.
    /// </summary>
    [Fact]
    public async Task V1RouteOfAnOldBinary_IsStillServedByTheCurrentTerminal_AndTheCurrentBinaryNeverCreatesOne()
    {
        var v1 = V1TerminalRoute(epoch: 1);
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(v1).Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsPreviousTerminalContractRoute(v1, ProjectionScopeStatusGAgent.ContractId)
            .Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                v1,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue("a materializer of a later contract serves every earlier terminal route");
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(v1)
            .Should().BeTrue("the binary that created it still serves it too");

        // The current binary creates no v1 route in any phase, and a v1 materializer would match
        // none of the routes it does create: the mixed fleet fails closed in both directions.
        foreach (var phase in AllPhases)
        {
            var built = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase);
            built.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2);
            built.ContractVersion.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);
            ProjectionScopeStatusRoutePolicy.IsPreviousTerminalContractRoute(built, ProjectionScopeStatusGAgent.ContractId)
                .Should().BeFalse();
        }

        // The real terminal materializer starts on the old binary's route and writes it.
        var world = new MixedBinaryWorld();
        (await world.PreviousBinaryTerminalWriteAsync(Publication(3, v1))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(Publication(3, v1)))
            .Should().Be(ProjectionWriteDisposition.Duplicate, "same epoch, same bytes: a duplicate, never a Conflict");
        world.Terminal.Agent.State.Active.Should().BeTrue();
        world.Terminal.Agent.State.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        world.Terminal.Agent.State.ContractVersion.Should().Be(ProjectionScopeStatusGAgent.ContractVersion);
        (await world.TerminalObserveAsync(Publication(4, v1))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(4, v1))).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(4, v1), WriteEvaluator.OldBinaryPlain))
            .Should().Be(ProjectionWriteDisposition.Duplicate, "the old binary's own evaluator agrees");
        await world.AssertStoreIsByteImageOfAsync(4, carriedRoute: v1);

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
    }

    private static readonly ProjectionScopeStatusRoutePhase[] AllPhases =
    [
        ProjectionScopeStatusRoutePhase.Unspecified,
        ProjectionScopeStatusRoutePhase.Warming,
        ProjectionScopeStatusRoutePhase.Blocked,
        ProjectionScopeStatusRoutePhase.Active,
    ];

    /// <summary>
    /// The capability advertisement of B1 (8d47b5e5c) and B2 (416e80f4a), verbatim
    /// (<c>git show 416e80f4a:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusTerminalCapabilityAdvertisement.cs</c>):
    /// capability and contract id of the v1 status terminal at their reader version 1.
    /// </summary>
    private static RuntimeFleetMemberCapability PreviousBinaryAdvertisedCapability() =>
        new()
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
            ReaderContractVersion = (int)PreviousBinaryProjectionScopeStatusTerminalWriter.ReaderVersion,
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
        };

    private static RuntimeFleetCapabilityQuiescenceEvidence CreateQuiescenceEvidence() =>
        new()
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 15,
            CapabilityEpoch = long.MaxValue,
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
            QuiescenceReaderContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion,
            QuiescedMembershipEpoch = 7,
            QuiescedMembershipDigest = "digest-a",
            QuiescedDeploymentRevision = "revision-a",
            QuiescedAt = Timestamp.FromDateTimeOffset(Now),
            QuiescenceTransitionId = "transition:quiesce",
        };

    private static RuntimeFleetCapabilityAdmission CreateActivationSealAdmission()
    {
        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            Status = RuntimeFleetCapabilityGateStatus.Open,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 16,
            CapabilityEpoch = 1,
            MembershipEpoch = 8,
            DeploymentRevision = "revision-b",
            MinimumReaderContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
            MembershipObservedAt = Timestamp.FromDateTimeOffset(Now.AddSeconds(-5)),
            MembershipValidUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(1)),
            ActiveMemberCount = 1,
            ConfirmedMemberCount = 1,
            MembershipDigest = "digest-b",
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
        };
        admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember
        {
            MemberId = "member-b",
            Incarnation = "inc-b",
        });
        return admission;
    }

    private static RuntimeActorStateSchemaAdoptionReceipt CreateActivationSealReceipt() =>
        new()
        {
            StateSchemaVersion = ProjectionScopeStatusGAgent.SupportedStateSchemaVersion,
            RequiredCapability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            RequiredContractId =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
            RequiredContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
            CapabilityEpoch = 1,
            AuthorityStateVersion = 16,
            MembershipEpoch = 8,
            DeploymentRevision = "revision-b",
            AdoptedAt = Timestamp.FromDateTimeOffset(Now),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = "digest-b",
            EvidenceStatus = RuntimeFleetCapabilityGateStatus.Open,
        };

    private static void AddPhaseBSeals(ProjectionScopeStatusRoute route)
    {
        route.ActivationSeals.Clear();
        route.ActivationSeals.Add(CreateActivationSeal(
            ProjectionScopeStatusActorRole.Source,
            SourceScopeActorId,
            SourceAgentKind));
        route.ActivationSeals.Add(CreateActivationSeal(
            ProjectionScopeStatusActorRole.LegacyWriter,
            LegacyActorId,
            LegacyWriterAgentKind));
        route.ActivationSeals.Add(CreateActivationSeal(
            ProjectionScopeStatusActorRole.TerminalWriter,
            TerminalActorId,
            ProjectionScopeStatusGAgent.AgentKind));
    }

    private static ProjectionScopeStatusActorSeal CreateActivationSeal(
        ProjectionScopeStatusActorRole role,
        string actorId,
        string agentKind) =>
        new()
        {
            Role = role,
            ActorId = actorId,
            AgentKind = agentKind,
            AdoptionReceipt = CreateActivationSealReceipt(),
        };

    /// <summary>
    /// VERBATIM decision rules of the OLD SOURCE binary's status-route activation
    /// (<c>git show 416e80f4a:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs</c>:
    /// <c>AdoptTerminalStatusRouteAsync</c>, <c>ReleaseLegacyStatusRouteIfPendingAsync</c>,
    /// <c>TryReleaseLegacyStatusShadowOfFlippedSourceAsync</c>; the gates from
    /// <c>ProjectionScopeStatusRoutePolicy</c> of the same commit). B1 (8d47b5e5c) shared them.
    /// That binary has no cutover phase — <c>phase</c> is field 7 and its
    /// <c>ProjectionScopeStatusRoute</c> had fields 1..6 — so a route it matches is ACTIVE by
    /// construction. Do not "modernize" these rules; their value is that they are the old binary.
    /// </summary>
    private static class PreviousBinarySourceScopeStatusRouteRules
    {
        /// <summary>Verbatim <c>IsTerminalRoute</c> of 416e80f4a: ANY non-empty route.</summary>
        public static bool IsTerminalRoute(ProjectionScopeStatusRoute? route) =>
            route != null &&
            route.RouteEpoch > 0 &&
            route.ContractVersion > 0 &&
            !string.IsNullOrWhiteSpace(route.ContractId);

        /// <summary>
        /// Verbatim <c>IsActiveTerminalRoute</c> of 416e80f4a, applied with that binary's own
        /// <c>ProjectionScopeStatusGAgent.ContractId</c> / <c>ContractVersion</c> (v1 / 1):
        /// an exact contract id AND an exact reader version, no phase.
        /// </summary>
        public static bool IsActiveTerminalRoute(ProjectionScopeStatusRoute? route) =>
            IsTerminalRoute(route) &&
            string.Equals(
                route!.ContractId,
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
                StringComparison.Ordinal) &&
            route.ContractVersion == PreviousBinaryProjectionScopeStatusTerminalWriter.ReaderVersion;

        /// <summary>Verbatim <c>AdoptTerminalStatusRouteAsync</c> of 416e80f4a, as a decision.</summary>
        public static PreviousBinarySourceActivation OnActivation(
            ProjectionScopeStatusRoute? route,
            bool freshV1Grant)
        {
            if (IsActiveTerminalRoute(route))
            {
                // UpsertTerminalStatusRelayAsync, CreateByKind if missing,
                // ReleaseLegacyStatusRouteIfPendingAsync (removes the legacy relay, dispatches
                // Release to the shadow and persists ProjectionScopeStatusLegacyRouteReleasedEvent
                // when IsTerminalRoute(route) && !route.LegacyRouteReleased),
                // ReconcileReappearedLegacyStatusRelayAsync — then return.
                var releases = !route!.LegacyRouteReleased;
                return new PreviousBinarySourceActivation(
                    TreatsRouteAsActive: true,
                    ReassertsTerminalRelay: true,
                    ReleasesLegacyShadow: releases,
                    PersistsLegacyRouteReleasedForEpoch: releases ? route.RouteEpoch : null,
                    AdoptsFreshTerminalRouteAtEpoch: null);
            }

            // "Projection scope status route stays legacy: no fresh terminal admission" plus a
            // backed-off durable retry; the committed route is not touched.
            if (!freshV1Grant)
                return new PreviousBinarySourceActivation(false, false, false, null, null);

            // A fresh v1 grant makes it build a v1 terminal route at the next epoch and commit it
            // ACTIVE immediately (ProjectionScopeStatusRouteActivatedEvent — no warming, no
            // blocked phase), then release the legacy shadow for that epoch.
            var adopted = (route?.RouteEpoch ?? 0) + 1;
            return new PreviousBinarySourceActivation(
                TreatsRouteAsActive: false,
                ReassertsTerminalRelay: true,
                ReleasesLegacyShadow: true,
                PersistsLegacyRouteReleasedForEpoch: adopted,
                AdoptsFreshTerminalRouteAtEpoch: adopted);
        }

        /// <summary>
        /// Verbatim <c>TryReleaseLegacyStatusShadowOfFlippedSourceAsync</c> of 416e80f4a: the
        /// legacy shadow scope releases itself as soon as the observed source carries ANY route.
        /// </summary>
        public static bool LegacyShadowReleasesItselfOnObserving(ProjectionScopeStatusRoute? route) =>
            IsTerminalRoute(route);
    }

    private sealed record PreviousBinarySourceActivation(
        bool TreatsRouteAsActive,
        bool ReassertsTerminalRelay,
        bool ReleasesLegacyShadow,
        long? PersistsLegacyRouteReleasedForEpoch,
        long? AdoptsFreshTerminalRouteAtEpoch);

    // ── routes ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The route B1 (8d47b5e5c) and B2 (416e80f4a) committed on adoption: the PREVIOUS terminal
    /// contract (<c>RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1</c>) at contract version 1
    /// (their reader version), phase-less, released legacy shadow. The current binary still serves
    /// it — <c>IsActiveTerminalRoute</c> accepts every earlier terminal contract — but never
    /// creates one.
    /// </summary>
    private static ProjectionScopeStatusRoute V1TerminalRoute(long epoch) =>
        new()
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
            ContractVersion = PreviousBinaryProjectionScopeStatusTerminalWriter.ReaderVersion,
            RouteEpoch = epoch,
            ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now),
            LegacyRouteReleased = true,
        };

    /// <summary>
    /// A route committed by B3: the CURRENT terminal contract
    /// (<see cref="ProjectionScopeStatusGAgent.ContractId"/>, reader version 2) with a phase, as
    /// <c>ProjectionScopeStatusRoutePolicy.BuildTerminalRoute</c> builds it. B1/B2 terminal gates
    /// (contract id <c>...terminal.v1</c> AND version exactly 1) match none of these.
    /// </summary>
    private static ProjectionScopeStatusRoute V2TerminalRoute(
        long epoch,
        ProjectionScopeStatusRoutePhase phase = ProjectionScopeStatusRoutePhase.Active)
    {
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(epoch, phase);
        route.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        route.LegacyRouteReleased = phase == ProjectionScopeStatusRoutePhase.Active;
        return route;
    }

    /// <summary>
    /// The shape a phased cutover committed BEFORE the contract revision: the previous
    /// (phase-unaware) terminal contract carrying a cutover phase. It is the counterexample route
    /// — an old binary's gate matches it and cannot see the phase (<c>phase</c> is field 7 and the
    /// <c>ProjectionScopeStatusRoute</c> of 8d47b5e5c/416e80f4a had fields 1..6). The current
    /// binary can no longer produce it: <c>BuildTerminalRoute</c> is v2 in every phase.
    /// </summary>
    private static ProjectionScopeStatusRoute PhasedPreviousContractRoute(
        long epoch,
        ProjectionScopeStatusRoutePhase phase) =>
        new()
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
            ContractVersion = PreviousBinaryProjectionScopeStatusTerminalWriter.ReaderVersion,
            RouteEpoch = epoch,
            Phase = phase,
            WarmStartedVersion = 4,
            ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };

    /// <summary>
    /// The in-place contract upgrade of a route of the previous terminal contract
    /// (<c>ProjectionScopeGAgentBase.StatusRoute.cs UpgradeTerminalRouteContractAsync</c>): same
    /// writer, current contract, strictly higher epoch, phase ACTIVE — replayed through the REAL
    /// applier so the epoch and phase rules are production's.
    /// </summary>
    private static ProjectionScopeStatusRoute UpgradeContractInPlace(
        ProjectionScopeStatusRoute route,
        long flipVersion)
    {
        ProjectionScopeStatusRoutePolicy.IsPreviousTerminalContractRoute(route, ProjectionScopeStatusGAgent.ContractId)
            .Should().BeTrue("only a route of an earlier terminal contract is upgraded in place");

        var upgraded = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            route.RouteEpoch + 1,
            ProjectionScopeStatusRoutePhase.Active);
        upgraded.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        upgraded.LegacyRouteReleased = route.LegacyRouteReleased;
        upgraded.FlipVersion = flipVersion;

        var next = ProjectionScopeStateApplier.ApplyStatusRouteContractUpgraded(
            new ProjectionScopeState { StatusRoute = route.Clone() },
            new ProjectionScopeStatusRouteContractUpgradedEvent
            {
                Route = upgraded,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
        next.StatusRoute.Should().NotBeNull();
        return next.StatusRoute!;
    }

    // ── source state / envelope builders ────────────────────────────────────────────────

    private static string DefaultEventId(long version) => $"source-evt-{version}";

    /// <summary>
    /// A rich source state so the full mapping surface (failure summary, per-actor maps in
    /// non-sorted insertion order, coordinates, in-flight source, routes, cutover) is covered.
    /// Every version has distinct bytes.
    /// </summary>
    private static ProjectionScopeState BuildSourceState(long version, ProjectionScopeStatusRoute? route)
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
        state.StatusRoute = route?.Clone();
        return state;
    }

    private static void AssertFullMappingSurface(ProjectionScopeStatusDocument document)
    {
        document.SourceVersions.Select(static s => s.SourceActorId).Should().Equal("actor-alpha", "actor-mike", "actor-zulu");
        document.LastSuccessfulSourceCoordinates.Select(static c => c.ActorId).Should().Equal("actor-alpha", "actor-zulu");
        document.RecentObservedEnvelopes.Should().HaveCount(2);
        document.UnresolvedFailureCount.Should().Be(2);
        document.RetryExhaustedFailureCount.Should().Be(1);
        document.InFlightSource.Should().NotBeNull();
        document.ActiveMaterializationRoute.Should().NotBeNull();
        document.MaterializationCutover.Should().NotBeNull();
    }

    private static Timestamp StateEventTimestamp(long version) =>
        Timestamp.FromDateTimeOffset(Now.AddSeconds(version));

    /// <summary>One committed publication of the source scope as every writer observes it.</summary>
    private sealed record SourcePublication(long Version, ProjectionScopeState State, string EventId, IMessage EventData);

    private static SourcePublication Publication(
        long version,
        ProjectionScopeStatusRoute? route,
        string? eventId = null,
        IMessage? eventData = null) =>
        Publication(version, BuildSourceState(version, route), eventId, eventData);

    private static SourcePublication Publication(
        long version,
        ProjectionScopeState state,
        string? eventId = null,
        IMessage? eventData = null) =>
        new(
            version,
            state,
            eventId ?? DefaultEventId(version),
            eventData ?? new ProjectionScopeWatermarkAdvancedEvent { LastSuccessfulVersion = state.LastSuccessfulVersion });

    private static StateEvent BuildStateEvent(SourcePublication publication) =>
        new()
        {
            AgentId = SourceScopeActorId,
            EventId = publication.EventId,
            Version = publication.Version,
            EventType = publication.EventData.Descriptor.FullName,
            Timestamp = StateEventTimestamp(publication.Version),
            EventData = Any.Pack(publication.EventData),
        };

    /// <summary>The source scope's committed publication as forwarded by its relay to one status writer.</summary>
    private static EventEnvelope BuildCommittedSourceEnvelope(string targetActorId, SourcePublication publication)
    {
        var original = new EventEnvelope
        {
            Id = $"outer-{targetActorId}-{publication.Version}-{publication.EventId}",
            Timestamp = StateEventTimestamp(publication.Version),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(SourceScopeActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = BuildStateEvent(publication),
                StateRoot = Any.Pack(publication.State),
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
            BuildCommittedSourceEnvelope(LegacyActorId, Publication(version, state)));
        return dispatcher.Upserts.Should().ContainSingle().Which;
    }

    private static async Task<ProjectionScopeStatusDocument> WriteThroughPreviousBinaryTerminalAsync(
        ProjectionScopeState state,
        long version,
        IProjectionClock clock)
    {
        var dispatcher = new RecordingOnlyStatusDispatcher();
        var terminal = new PreviousBinaryProjectionScopeStatusTerminalWriter(dispatcher, clock);
        (await terminal.ProjectAsync(BuildCommittedSourceEnvelope(TerminalActorId, Publication(version, state))))
            .Should().BeTrue("B1's gate accepts the route");
        return dispatcher.Upserts.Should().ContainSingle().Which;
    }

    /// <summary>B1's mapper (identical code to B3's) for one publication, without its route gate.</summary>
    private static ProjectionScopeStatusDocument MapThroughPreviousBinary(SourcePublication publication)
    {
        var envelope = BuildCommittedSourceEnvelope(TerminalActorId, publication);
        CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var unpackedState)
            .Should().BeTrue();
        return PreviousBinaryProjectionScopeStatusMapper.Map(
            unpackedState!,
            stateEvent!,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, Now));
    }

    private static ProjectionScopeStatusDocument MapThroughNewBinary(ProjectionScopeState state, long version) =>
        MapThroughNewBinary(Publication(version, state));

    private static ProjectionScopeStatusDocument MapThroughNewBinary(SourcePublication publication)
    {
        var envelope = BuildCommittedSourceEnvelope(TerminalActorId, publication);
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

    // ── the mixed-binary world: one store, four writers, two evaluators ─────────────────

    public enum StatusWriter
    {
        /// <summary>B0 (b64c96a45) legacy shadow; also the byte shape of every B2 (416e80f4a) write.</summary>
        OldBinary,

        /// <summary>B1 (8d47b5e5c) terminal materializer: route carried, v1 gate.</summary>
        PreviousBinaryTerminal,

        /// <summary>B2 (416e80f4a) legacy shadow projector: skips every routed source, B0 byte shape.</summary>
        PreviousBinaryLegacy,

        /// <summary>B3 gated legacy shadow projector (real <see cref="ProjectionScopeStatusProjector"/>).</summary>
        NewBinaryLegacy,

        /// <summary>B3 terminal materializer (real <see cref="ProjectionScopeStatusGAgent"/>).</summary>
        Terminal,
    }

    public enum WriteEvaluator
    {
        /// <summary>The shared store's (current, epoch-fenced) evaluator decides and applies.</summary>
        CurrentStore,

        /// <summary>
        /// The old binary's plain byte evaluator decides against the same stored document (what B0,
        /// B1 and B2 log on their side); only an Applied decision reaches the store.
        /// </summary>
        OldBinaryPlain,
    }

    private sealed record StatusWrite(
        StatusWriter Writer,
        WriteEvaluator Evaluator,
        long Version,
        string EventId,
        ProjectionWriteDisposition Disposition);

    /// <summary>
    /// The one status document store shared by every writer of the rolling window, plus the
    /// ordered journal of every write decision it produced.
    /// </summary>
    private sealed class StatusStoreJournal
    {
        public InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string> Store { get; } =
            new(static document => document.Id);

        public List<StatusWrite> Writes { get; } = [];

        public IProjectionWriteDispatcher<ProjectionScopeStatusDocument> DispatcherFor(StatusWriter writer, WriteEvaluator evaluator) =>
            evaluator == WriteEvaluator.CurrentStore
                ? new CurrentStoreDispatcher(this, writer)
                : new OldBinaryPlainEvaluatorDispatcher(this, writer);

        private sealed class CurrentStoreDispatcher : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
        {
            private readonly StatusStoreJournal _journal;
            private readonly StatusWriter _writer;

            public CurrentStoreDispatcher(StatusStoreJournal journal, StatusWriter writer)
            {
                _journal = journal;
                _writer = writer;
            }

            public async Task<ProjectionWriteResult> UpsertAsync(
                ProjectionScopeStatusDocument readModel,
                CancellationToken ct = default)
            {
                var result = await _journal.Store.UpsertAsync(readModel, ct);
                _journal.Writes.Add(new StatusWrite(_writer, WriteEvaluator.CurrentStore, readModel.StateVersion, readModel.LastEventId, result.Disposition));
                return result;
            }

            public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
                throw new NotSupportedException("status writers never delete");
        }

        /// <summary>
        /// An old binary evaluates client-side against the shared store (as the Elasticsearch
        /// optimistic writer does): read the stored document, decide with its plain evaluator,
        /// write only on Applied. The current store agrees on every Applied of the plain
        /// evaluator (a strictly higher version, or no document yet).
        /// </summary>
        private sealed class OldBinaryPlainEvaluatorDispatcher : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
        {
            private readonly StatusStoreJournal _journal;
            private readonly StatusWriter _writer;

            public OldBinaryPlainEvaluatorDispatcher(StatusStoreJournal journal, StatusWriter writer)
            {
                _journal = journal;
                _writer = writer;
            }

            public async Task<ProjectionWriteResult> UpsertAsync(
                ProjectionScopeStatusDocument readModel,
                CancellationToken ct = default)
            {
                var existing = await _journal.Store.GetAsync(readModel.Id, ct);
                var decision = PreviousBinaryProjectionWriteResultEvaluator.Evaluate(existing, readModel);
                if (decision.IsApplied)
                {
                    var stored = await _journal.Store.UpsertAsync(readModel, ct);
                    stored.Disposition.Should().Be(ProjectionWriteDisposition.Applied, "the current store applies every write the plain evaluator applies");
                }

                _journal.Writes.Add(new StatusWrite(_writer, WriteEvaluator.OldBinaryPlain, readModel.StateVersion, readModel.LastEventId, decision.Disposition));
                return decision;
            }

            public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
                throw new NotSupportedException("status writers never delete");
        }
    }

    private sealed class MixedBinaryWorld
    {
        private readonly FixedProjectionClock _clock = new(Now);
        private readonly LegacyBinaryProjectionScopeStatusProjector _oldBinary;
        private readonly LegacyBinaryProjectionScopeStatusProjector _oldBinaryOnPlainEvaluator;
        private readonly PreviousBinaryProjectionScopeStatusTerminalWriter _previousBinaryTerminal;
        private readonly PreviousBinaryProjectionScopeStatusTerminalWriter _previousBinaryTerminalOnPlainEvaluator;
        private readonly PreviousBinaryRouteGatedLegacyProjector _previousBinaryLegacy;
        private readonly ProjectionScopeStatusProjector _newBinaryLegacy;
        private readonly ProjectionScopeStatusMaterializationContext _legacyContext =
            new() { RootActorId = SourceScopeActorId };

        public MixedBinaryWorld(bool terminalBridgeQuiesced = false)
        {
            _oldBinary = new LegacyBinaryProjectionScopeStatusProjector(
                Journal.DispatcherFor(StatusWriter.OldBinary, WriteEvaluator.CurrentStore),
                _clock);
            _oldBinaryOnPlainEvaluator = new LegacyBinaryProjectionScopeStatusProjector(
                Journal.DispatcherFor(StatusWriter.OldBinary, WriteEvaluator.OldBinaryPlain),
                _clock);
            _previousBinaryTerminal = new PreviousBinaryProjectionScopeStatusTerminalWriter(
                Journal.DispatcherFor(StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore),
                _clock);
            _previousBinaryTerminalOnPlainEvaluator = new PreviousBinaryProjectionScopeStatusTerminalWriter(
                Journal.DispatcherFor(StatusWriter.PreviousBinaryTerminal, WriteEvaluator.OldBinaryPlain),
                _clock);
            _previousBinaryLegacy = new PreviousBinaryRouteGatedLegacyProjector(
                Journal.DispatcherFor(StatusWriter.PreviousBinaryLegacy, WriteEvaluator.CurrentStore),
                _clock);
            _newBinaryLegacy = new ProjectionScopeStatusProjector(
                Journal.DispatcherFor(StatusWriter.NewBinaryLegacy, WriteEvaluator.CurrentStore),
                _clock);
            Terminal = TerminalAgentHarness.Build(
                Journal.DispatcherFor(StatusWriter.Terminal, WriteEvaluator.CurrentStore),
                _clock,
                terminalBridgeQuiesced ? CreateQuiescenceEvidence() : null);
        }

        public StatusStoreJournal Journal { get; } = new();
        public TerminalAgentHarness Terminal { get; }

        public void QuiesceTerminalBridge() => Terminal.EnablePhaseB();

        /// <summary>B0 legacy shadow (and B2 byte shape): writes every delivered version without a route.</summary>
        public async Task<ProjectionWriteDisposition> OldBinaryWriteAsync(
            SourcePublication publication,
            WriteEvaluator evaluator = WriteEvaluator.CurrentStore)
        {
            var before = Journal.Writes.Count;
            var writer = evaluator == WriteEvaluator.CurrentStore ? _oldBinary : _oldBinaryOnPlainEvaluator;
            await writer.ProjectAsync(_legacyContext, BuildCommittedSourceEnvelope(LegacyActorId, publication));
            return SingleNewWrite(before, StatusWriter.OldBinary, evaluator).Disposition;
        }

        /// <summary>B1 terminal: writes only for a v1 terminal route; null when its gate refused.</summary>
        public async Task<ProjectionWriteDisposition?> PreviousBinaryTerminalWriteAsync(
            SourcePublication publication,
            WriteEvaluator evaluator = WriteEvaluator.CurrentStore)
        {
            var before = Journal.Writes.Count;
            var writer = evaluator == WriteEvaluator.CurrentStore ? _previousBinaryTerminal : _previousBinaryTerminalOnPlainEvaluator;
            var wrote = await writer.ProjectAsync(BuildCommittedSourceEnvelope(TerminalActorId, publication));
            if (!wrote)
            {
                Journal.Writes.Should().HaveCount(before);
                return null;
            }

            return SingleNewWrite(before, StatusWriter.PreviousBinaryTerminal, evaluator).Disposition;
        }

        /// <summary>B2 legacy shadow projector: skips every routed source; null when it did not write.</summary>
        public async Task<ProjectionWriteDisposition?> PreviousBinaryLegacyWriteAsync(SourcePublication publication)
        {
            var before = Journal.Writes.Count;
            await _previousBinaryLegacy.ProjectAsync(_legacyContext, BuildCommittedSourceEnvelope(LegacyActorId, publication));
            return Journal.Writes.Count == before
                ? null
                : SingleNewWrite(before, StatusWriter.PreviousBinaryLegacy, WriteEvaluator.CurrentStore).Disposition;
        }

        /// <summary>B3 legacy shadow projector: gated by the source's committed route; null when it did not write.</summary>
        public async Task<ProjectionWriteDisposition?> NewBinaryLegacyWriteAsync(SourcePublication publication)
        {
            var before = Journal.Writes.Count;
            await _newBinaryLegacy.ProjectAsync(_legacyContext, BuildCommittedSourceEnvelope(LegacyActorId, publication));
            return Journal.Writes.Count == before
                ? null
                : SingleNewWrite(before, StatusWriter.NewBinaryLegacy, WriteEvaluator.CurrentStore).Disposition;
        }

        /// <summary>The real B3 terminal materializer observing the source's forwarded publication; null when it did not write.</summary>
        public async Task<ProjectionWriteDisposition?> TerminalObserveAsync(SourcePublication publication)
        {
            var before = Journal.Writes.Count;
            await Terminal.Agent.HandleObservedEnvelopeAsync(BuildCommittedSourceEnvelope(TerminalActorId, publication));
            return Journal.Writes.Count == before
                ? null
                : SingleNewWrite(before, StatusWriter.Terminal, WriteEvaluator.CurrentStore).Disposition;
        }

        /// <summary>
        /// The real B3 terminal materializer observing a publication whose write the store
        /// rejects: the observation FAILS (<see cref="ProjectionScopeStatusWriteRejectedException"/>)
        /// so the provider redelivers it without advancing its checkpoint, and nothing about the
        /// observation is persisted.
        /// </summary>
        public async Task<ProjectionScopeStatusWriteRejectedException> TerminalObserveRejectedAsync(
            SourcePublication publication)
        {
            var before = Journal.Writes.Count;
            var envelope = BuildCommittedSourceEnvelope(TerminalActorId, publication);
            Func<Task> observe = () => Terminal.Agent.HandleObservedEnvelopeAsync(envelope);
            var rejection = (await observe.Should().ThrowAsync<ProjectionScopeStatusWriteRejectedException>()).Which;
            SingleNewWrite(before, StatusWriter.Terminal, WriteEvaluator.CurrentStore)
                .Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
            return rejection;
        }

        /// <summary>The current store never produced a Conflict or Gap for any writer.</summary>
        public void AssertNoConflictFromTheCurrentStore()
        {
            var currentStoreWrites = Journal.Writes.Where(static w => w.Evaluator == WriteEvaluator.CurrentStore).ToList();
            currentStoreWrites.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Conflict);
            currentStoreWrites.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Gap);
        }

        /// <summary>
        /// The store holds exactly the byte image of the given version as written by a binary that
        /// carries <paramref name="carriedRoute"/> in the document (null for B0/B2 writes).
        /// </summary>
        public async Task AssertStoreIsByteImageOfAsync(long version, ProjectionScopeStatusRoute? carriedRoute, string? eventId = null)
        {
            var stored = await Journal.Store.GetAsync(SourceScopeActorId);
            stored.Should().NotBeNull();
            stored!.StateVersion.Should().Be(version);
            stored.LastEventId.Should().Be(eventId ?? DefaultEventId(version));
            var expected = MapThroughNewBinary(Publication(version, carriedRoute, eventId));
            stored.ToByteArray().Should().Equal(expected.ToByteArray());
            ((IProjectionRouteFencedReadModel)stored).RouteEpoch.Should().Be(carriedRoute?.RouteEpoch ?? 0);
        }

        /// <summary>
        /// One source scope has exactly ONE status document, whichever binaries wrote it: a mixed
        /// fleet never forks the document into a second key (every writer of every binary resolves
        /// the same <see cref="ProjectionScopeActorId"/> from the same source state).
        /// </summary>
        public async Task AssertExactlyOneStoredDocumentAsync()
        {
            var all = await Journal.Store.QueryAsync(new ProjectionDocumentQuery
            {
                Take = 10,
                IncludeTotalCount = true,
            });
            all.TotalCount.Should().Be(1);
            all.Items.Should().ContainSingle().Which.Id.Should().Be(SourceScopeActorId);
        }

        private StatusWrite SingleNewWrite(int before, StatusWriter writer, WriteEvaluator evaluator)
        {
            Journal.Writes.Should().HaveCount(before + 1, $"{writer} performs exactly one write per delivered envelope");
            var write = Journal.Writes[^1];
            write.Writer.Should().Be(writer);
            write.Evaluator.Should().Be(evaluator);
            return write;
        }
    }

    // ── terminal materializer harness (real ProjectionScopeStatusGAgent) ────────────────

    private sealed class TerminalAgentHarness
    {
        public required ProjectionScopeStatusGAgent Agent { get; init; }
        public required TrackingEventSourcing<ProjectionScopeStatusTerminalState> EventSourcing { get; init; }
        public required RecordingEventPublisher Outbox { get; init; }
        public required MutableQuiescenceReader QuiescenceReader { get; init; }
        public required MutableAdmissionReader AdmissionReader { get; init; }
        public required MutableSchemaContextReader SchemaContextReader { get; init; }

        /// <summary>Caught-up reports the warming terminal sent to its source scope.</summary>
        public IReadOnlyList<ProjectionScopeStatusWriterCaughtUpEvent> CaughtUpReports =>
            Outbox.Sent
                .Where(static s => s.TargetActorId == SourceScopeActorId)
                .Select(static s => s.Message)
                .OfType<ProjectionScopeStatusWriterCaughtUpEvent>()
                .ToList();

        public static TerminalAgentHarness Build(
            IProjectionWriteDispatcher<ProjectionScopeStatusDocument> dispatcher,
            IProjectionClock clock,
            RuntimeFleetCapabilityQuiescenceEvidence? quiescence)
        {
            var outbox = new RecordingEventPublisher();
            var agent = new ProjectionScopeStatusGAgent { EventPublisher = outbox };
            typeof(GAgentBase)
                .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(agent, TerminalActorId);
            var eventSourcing = new TrackingEventSourcing<ProjectionScopeStatusTerminalState>(TransitionTerminal);
            agent.EventSourcing = eventSourcing;

            var services = new ServiceCollection();
            var quiescenceReader = new MutableQuiescenceReader(quiescence);
            var admissionReader = new MutableAdmissionReader(
                quiescence == null ? null : CreateActivationSealAdmission());
            var schemaContextReader = new MutableSchemaContextReader(
                quiescence == null ? null : CreateTerminalSchemaContext());
            services.AddSingleton(dispatcher);
            services.AddSingleton(clock);
            services.AddSingleton<IStreamProvider>(new NoRelayStreamProvider());
            services.AddSingleton<IRuntimeFleetCapabilityQuiescenceReader>(quiescenceReader);
            services.AddSingleton<IRuntimeFleetCapabilityAdmissionReader>(admissionReader);
            services.AddSingleton<IRuntimeLocalMembershipIdentityReader>(admissionReader);
            services.AddSingleton<IRuntimeActorStateSchemaContextReader>(schemaContextReader);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            agent.Services = services.BuildServiceProvider();
            return new TerminalAgentHarness
            {
                Agent = agent,
                EventSourcing = eventSourcing,
                Outbox = outbox,
                QuiescenceReader = quiescenceReader,
                AdmissionReader = admissionReader,
                SchemaContextReader = schemaContextReader,
            };
        }

        public void EnablePhaseB()
        {
            QuiescenceReader.Evidence = CreateQuiescenceEvidence();
            AdmissionReader.Admission = CreateActivationSealAdmission();
            SchemaContextReader.Current = CreateTerminalSchemaContext();
        }

        private static RuntimeActorStateSchemaContext CreateTerminalSchemaContext() =>
            new(
                ProjectionScopeStatusGAgent.AgentKind,
                ProjectionScopeStatusGAgent.SupportedStateSchemaVersion,
                [CreateActivationSealReceipt()]);

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

    private sealed class MutableQuiescenceReader(RuntimeFleetCapabilityQuiescenceEvidence? evidence)
        : IRuntimeFleetCapabilityQuiescenceReader
    {
        public RuntimeFleetCapabilityQuiescenceEvidence? Evidence { get; set; } = evidence?.Clone();

        public Task<RuntimeFleetCapabilityQuiescenceEvidence?> GetQuiescenceAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                Evidence?.Capability == capability ? Evidence.Clone() : null);
        }
    }

    private sealed class MutableAdmissionReader(RuntimeFleetCapabilityAdmission? admission)
        : IRuntimeFleetCapabilityAdmissionReader,
            IRuntimeLocalMembershipIdentityReader
    {
        public RuntimeFleetCapabilityAdmission? Admission { get; set; } = admission?.Clone();

        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                Admission?.Capability == capability ? Admission.Clone() : null);
        }

        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(new RuntimeLocalMembershipIdentity(
                8,
                "digest-b",
                "revision-b",
                "member-b",
                "inc-b"));
        }
    }

    private sealed class MutableSchemaContextReader(RuntimeActorStateSchemaContext? current)
        : IRuntimeActorStateSchemaContextReader
    {
        public RuntimeActorStateSchemaContext? Current { get; set; } = current;
    }

    private sealed record SentMessage(string TargetActorId, IMessage Message);

    /// <summary>Records what the terminal addresses to another actor (its warming caught-up reports).</summary>
    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<SentMessage> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException("the terminal materializer publishes nothing to its topology");

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Sent.Add(new SentMessage(targetActorId, evt));
            return Task.CompletedTask;
        }
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

    /// <summary>
    /// VERBATIM fixture (B2): the route gate of
    /// <c>git show 416e80f4a:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusProjector.cs</c>
    /// — "once the source has committed a terminal route this legacy shadow scope is no longer an
    /// authoritative writer", where that binary's <c>IsTerminalRoute</c> matches ANY non-empty
    /// route. The document it writes is the B0 byte shape (field 30 was <c>reserved</c> at that
    /// commit), so the write itself is delegated to
    /// <see cref="LegacyBinaryProjectionScopeStatusProjector"/>. Do not "modernize" this gate.
    /// </summary>
    private sealed class PreviousBinaryRouteGatedLegacyProjector
        : ICurrentStateProjectionMaterializer<ProjectionScopeStatusMaterializationContext>
    {
        private readonly LegacyBinaryProjectionScopeStatusProjector _write;

        public PreviousBinaryRouteGatedLegacyProjector(
            IProjectionWriteDispatcher<ProjectionScopeStatusDocument> writeDispatcher,
            IProjectionClock clock)
        {
            _write = new LegacyBinaryProjectionScopeStatusProjector(writeDispatcher, clock);
        }

        public static bool WritesFor(ProjectionScopeStatusRoute? route) =>
            !PreviousBinarySourceScopeStatusRouteRules.IsTerminalRoute(route);

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

            if (!WritesFor(state.StatusRoute))
                return;

            await _write.ProjectAsync(context, envelope, ct);
        }
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
/// VERBATIM fixture (B0): <c>src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusProjector.cs</c>
/// as of commit b64c96a45 (<c>git show b64c96a45:...</c>), the last production binary before the
/// terminal status materializer. Only the class/constructor name differs and the
/// <c>[ProjectionExempt]</c> registration marker is omitted (a test fixture is not a registered
/// production materializer). It builds the document field by field, in the same order, and
/// writes it unconditionally: it has no notion of <c>ProjectionScopeState.status_route</c>
/// (an unknown field to that binary) and no route gate, and it never writes document field 30.
/// It is also the byte shape of every write of B2 (416e80f4a), whose document had field 30
/// reserved. Do not "modernize" this class; its value is that it is the old binary.
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

/// <summary>
/// VERBATIM fixture (B1): <c>src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusDocumentMapper.cs</c>
/// as of commit 8d47b5e5c (<c>git show 8d47b5e5c:...</c>), the binary whose status document
/// carried <c>status_route = 30</c>: the mapping copies <c>state.StatusRoute</c> into the document,
/// exactly as today's mapper does (the code is identical up to the doc comment; both writers of
/// that binary, legacy shadow and terminal materializer, shared it). Only the class name differs.
/// The <c>ProjectionScopeStatusRoute</c> of that binary had fields 1..6 (no phase / warm / caught-up /
/// flip fields); its terminal routes were v1: contract version 1, epoch 1, phase-less. Do not
/// "modernize" this class; its value is that it is the old binary.
/// </summary>
internal static class PreviousBinaryProjectionScopeStatusMapper
{
    public static ProjectionScopeStatusDocument Map(
        ProjectionScopeState state,
        StateEvent stateEvent,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stateEvent);

        var scopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            state.RootActorId,
            state.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(state.Mode),
            state.SessionId));
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
            StatusRoute = state.StatusRoute?.Clone(),
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

        return document;
    }
}

/// <summary>
/// Fixture (B1): the terminal write path of commit 8d47b5e5c reduced to its write decision —
/// the route gate of <c>ProjectionScopeStatusGAgent.HandleObservedEnvelopeAsync</c> at that commit
/// (<c>ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute</c> of 8d47b5e5c, copied verbatim in
/// <see cref="IsActiveTerminalRoute"/>: a non-empty route whose contract id matches AND whose
/// <c>ContractVersion == 1</c>, the reader version of that binary
/// (<c>git show 8d47b5e5c:src/Aevatar.Foundation.Abstractions/Runtime/IRuntimeFleetCapabilityAdmissionReader.cs</c>
/// line 29); that binary had no phases), the terminal-outcome filter, and the write of
/// <see cref="PreviousBinaryProjectionScopeStatusMapper"/>'s document through the dispatcher. The
/// same gate (<c>== 1</c>) is B2's (416e80f4a); its evaluator was
/// <see cref="PreviousBinaryProjectionWriteResultEvaluator"/>. Lifecycle, relays and deferred
/// retries of that actor are out of scope here.
/// </summary>
internal sealed class PreviousBinaryProjectionScopeStatusTerminalWriter
{
    /// <summary>
    /// <c>RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion</c> as those
    /// two binaries defined it. It is a historical fact about B1/B2, NOT the current constant of
    /// the same name: the contract revision moved B3 to the v2 contract at reader version 2, so a
    /// route B3 commits is deliberately NOT serviceable by these binaries after a rollback (that
    /// is the point of the revision — the phase-unaware writer must not take a phased route over).
    /// Asserted against production in
    /// <see cref="ProjectionScopeStatusMixedBinaryTests.V1RouteOfAnOldBinary_IsStillServedByTheCurrentTerminal_AndTheCurrentBinaryNeverCreatesOne"/>.
    /// </summary>
    public const long ReaderVersion = 1;

    private readonly IProjectionWriteDispatcher<ProjectionScopeStatusDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public PreviousBinaryProjectionScopeStatusTerminalWriter(
        IProjectionWriteDispatcher<ProjectionScopeStatusDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Verbatim <c>IsActiveTerminalRoute(route, ContractId, ContractVersion)</c> of 8d47b5e5c (and 416e80f4a).</summary>
    public static bool IsActiveTerminalRoute(ProjectionScopeStatusRoute? route) =>
        route != null &&
        route.RouteEpoch > 0 &&
        route.ContractVersion > 0 &&
        !string.IsNullOrWhiteSpace(route.ContractId) &&
        string.Equals(route.ContractId, RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1, StringComparison.Ordinal) &&
        route.ContractVersion == ReaderVersion;

    /// <summary>Returns whether the old terminal wrote for this publication.</summary>
    public async ValueTask<bool> ProjectAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var sourceState) ||
            stateEvent?.EventData == null ||
            sourceState == null)
        {
            return false;
        }

        if (!IsActiveTerminalRoute(sourceState.StatusRoute))
            return false;

        if (!ProjectionScopeStatusRoutePolicy.IsTerminalOutcome(stateEvent.EventData))
            return false;

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        await _writeDispatcher.UpsertAsync(
            PreviousBinaryProjectionScopeStatusMapper.Map(sourceState, stateEvent, updatedAt),
            ct);
        return true;
    }
}

/// <summary>
/// VERBATIM fixture: <c>src/Aevatar.CQRS.Projection.Stores.Abstractions/Abstractions/ReadModels/ProjectionWriteResultEvaluator.cs</c>
/// as of b64c96a45, 8d47b5e5c and 416e80f4a (byte-identical in all three; one plain evaluator for
/// B0, B1 and B2). Only the class name differs. Same version: the event ids must match (else
/// Conflict) and the protobuf bytes must be identical (Duplicate) else Conflict; lower version
/// Stale; higher version Applied. It knows no route epoch. Old binaries evaluate client-side against
/// the shared store, so this is what they log for their own writes.
/// </summary>
internal static class PreviousBinaryProjectionWriteResultEvaluator
{
    public static ProjectionWriteResult Evaluate(
        IProjectionReadModel? existing,
        IProjectionReadModel incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (string.IsNullOrWhiteSpace(incoming.Id))
            throw new InvalidOperationException("Projection read model id must be non-empty.");
        if (string.IsNullOrWhiteSpace(incoming.ActorId))
            throw new InvalidOperationException("Projection read model actor id must be non-empty.");

        if (existing == null)
            return ProjectionWriteResult.Applied();

        if (!string.Equals(existing.ActorId, incoming.ActorId, StringComparison.Ordinal))
            return ProjectionWriteResult.Conflict();

        if (incoming.StateVersion < existing.StateVersion)
            return ProjectionWriteResult.Stale();

        if (incoming.StateVersion == existing.StateVersion)
        {
            if (!string.Equals(existing.LastEventId, incoming.LastEventId, StringComparison.Ordinal))
                return ProjectionWriteResult.Conflict();

            if (existing is IMessage existingMessage && incoming is IMessage incomingMessage)
            {
                return existingMessage.Descriptor.FullName == incomingMessage.Descriptor.FullName &&
                       existingMessage.ToByteString().Equals(incomingMessage.ToByteString())
                    ? ProjectionWriteResult.Duplicate()
                    : ProjectionWriteResult.Conflict();
            }

            // Legacy non-protobuf read models cannot prove byte equivalence.
            // Preserve their historical same-event idempotency behavior while
            // every typed projection read model uses the strict branch above.
            return ProjectionWriteResult.Applied();
        }

        return ProjectionWriteResult.Applied();
    }
}
