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
/// <see cref="IProjectionRouteFencedReadModel"/> (route epoch 0 when absent); the terminal reader
/// version stays 1, so every route B3 commits (fresh adoption, re-adoption at a higher epoch,
/// any phase) is a v1 route that a B1/B2 terminal can serve after a rollback (their gate ignores
/// the phase); <see cref="ProjectionWriteResultEvaluator"/> is epoch
/// fenced at the same source version; the legacy shadow projector is gated by
/// <c>ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite</c>; the source drives a phased
/// cutover (WARMING, caught up, BLOCKED, previous writer released, ACTIVE).</item>
/// </list>
/// Provenance commands: <c>git show b64c96a45:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusProjector.cs</c>,
/// <c>git show 8d47b5e5c:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusDocumentMapper.cs</c>,
/// <c>git show 8d47b5e5c:src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusRoutePolicy.cs</c>,
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
/// <item><b>Rolling forward</b>: epoch-0 documents (B0/B2) are taken over at the same version by the
/// first epoch-1 write (B3 legacy shadow while warming, or B3 terminal from BLOCKED on); the
/// takeover repeats as a duplicate; every higher version is applied whoever writes it. B1 and B3
/// on the same v1 route produce identical bytes at every version: always duplicates.</item>
/// <item><b>Delayed legacy delivery</b>: an old-binary write for a version the store already holds
/// under a higher epoch is Stale under the current evaluator (Conflict only in the old binary's
/// own log, store untouched); a lower version is Stale; a version the new writer has not processed
/// yet is applied and the new writer's later write at that version takes it over (or duplicates
/// it). The store always holds the byte image of the highest version.</item>
/// <item><b>Rollback / drain</b>: an old binary after rollback writes higher versions with a lower
/// epoch (version wins, Applied); its same-version redelivery over a higher-epoch document is
/// Stale/Conflict-in-its-own-log; rolling forward again the new writer takes over at the same
/// version and continues. Revocation rolls the route to the legacy writer through the same phases
/// at the next epoch: the terminal writes while the legacy route warms, releases itself when it
/// blocks, and the legacy shadow writes from BLOCKED on. Never a Conflict from the current store,
/// one authoritative document.</item>
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
            .Should().BeTrue("B3 is a v1 reader and writes v1 routes");
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(route)
            .Should().BeTrue("B1 wrote this route");

        var previous = await WriteThroughPreviousBinaryTerminalAsync(state, version, new FixedProjectionClock(Now));
        var current = MapThroughNewBinary(state, version);

        var previousBytes = previous.ToByteArray();
        previousBytes.Should().NotBeEmpty();
        current.ToByteArray().Should().Equal(previousBytes, "same (state, stateEvent, timestamp) gives the same bytes on B1 and B3");
        current.StatusRoute.Should().NotBeNull();
        current.StatusRoute.RouteEpoch.Should().Be(1);
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
        var routedState = BuildSourceState(version, TerminalRoute(epoch: 1));

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

        // Rollback safety: a source that adopts the terminal writer FRESH under B3 (phased cutover)
        // also gets a v1 route, which B2's terminal gate accepts in any phase — so a rollback to B2
        // after a fresh B3 adoption still has a writer (see the next test).
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(TerminalRoute(epoch: 1)).Should().BeTrue();
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(TerminalRoute(epoch: 2, ProjectionScopeStatusRoutePhase.Blocked)).Should().BeTrue();
    }

    [Fact]
    public async Task RollbackToB2AfterAFreshB3Adoption_B2TerminalKeepsWriting_HigherVersionWins_SameVersionNeverCorruptsTheStore()
    {
        // The route a B3 phased cutover committed: v1 contract, epoch 1, phase ACTIVE.
        var route = TerminalRoute(epoch: 1);
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(route).Should().BeTrue("B2's gate ignores the phase");
        var world = new MixedBinaryWorld();

        // B3's terminal owns v8 (document carries the route, epoch 1).
        (await world.TerminalObserveAsync(Publication(8, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(8, carriedRoute: route);

        // Rollback to B2: its terminal (route-less bytes, plain evaluator) redelivers v8 — Conflict
        // on B2's side only (Stale on B3's side), store untouched — then writes v9: version wins.
        (await world.OldBinaryWriteAsync(Publication(8, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Conflict);
        (await world.OldBinaryWriteAsync(Publication(8, route))).Should().Be(ProjectionWriteDisposition.Stale);
        await world.AssertStoreIsByteImageOfAsync(8, carriedRoute: route);
        (await world.OldBinaryWriteAsync(Publication(9, route), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(9, carriedRoute: null);

        // Roll forward again: B3's terminal takes v9 over (epoch 1 > 0) and continues.
        (await world.TerminalObserveAsync(Publication(9, route))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(Publication(10, route))).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(10, carriedRoute: route);

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
    }

    // ── 4. rolling 8d47b5e5c (B1) <-> B3 on a v1 route ─────────────────────────────────

    [Fact]
    public async Task RollingB1AndB3OnV1Route_AreDuplicatesAtEveryVersion_AndReadoptionAtAHigherEpochTakesOver()
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

        // Re-adoption at a strictly higher epoch under B3: the source commits another v1 terminal
        // route at epoch 2 (any strictly higher epoch behaves the same; phase ACTIVE). B1's gate
        // accepts every v1 route whatever its phase, so B1's terminal writes it too — same mapper,
        // same route: byte-identical, duplicates in both orders.
        var readopted = TerminalRoute(epoch: 2);
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(readopted).Should().BeTrue();
        (await world.TerminalObserveAsync(Publication(6, readopted))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(6, readopted))).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(6, readopted), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Duplicate);
        await world.AssertStoreIsByteImageOfAsync(6, carriedRoute: readopted);
        (await world.PreviousBinaryTerminalWriteAsync(Publication(7, readopted), WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Applied, "B1 first");
        (await world.TerminalObserveAsync(Publication(7, readopted))).Should().Be(ProjectionWriteDisposition.Duplicate, "B3 second");
        await world.AssertStoreIsByteImageOfAsync(7, carriedRoute: readopted);

        // Evaluator-level: a B1-shaped epoch-1 document at the re-adoption version (a late B1
        // write that still carries the previous route — not producible from one committed source
        // history, one version has one route) is Stale under the current evaluator — never a
        // conflict; only B1's own plain evaluator would log Conflict. And B3's epoch-2 document
        // takes over an epoch-1 B1 document at the same version.
        var stored = (await world.Journal.Store.GetAsync(SourceScopeActorId))!;
        var lateB1 = await WriteThroughPreviousBinaryTerminalAsync(BuildSourceState(7, route), 7, new FixedProjectionClock(Now));
        ProjectionWriteResultEvaluator.Evaluate(stored, lateB1).Disposition.Should().Be(ProjectionWriteDisposition.Stale);
        PreviousBinaryProjectionWriteResultEvaluator.Evaluate(stored, lateB1).Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
        ProjectionWriteResultEvaluator.Evaluate(lateB1, stored).Disposition.Should().Be(ProjectionWriteDisposition.Applied);

        (await world.PreviousBinaryTerminalWriteAsync(Publication(5, route))).Should().Be(ProjectionWriteDisposition.Stale, "a B1 straggler of a lower version");

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        await world.AssertStoreIsByteImageOfAsync(7, carriedRoute: readopted);
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
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore, 6, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.OldBinaryPlain, 6, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.OldBinaryPlain, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 7, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.PreviousBinaryTerminal, WriteEvaluator.CurrentStore, 5, ProjectionWriteDisposition.Stale));
    }

    // ── 4b. old-binary terminal on an old silo during a B3 WARMING cutover ──────────────

    [Fact]
    public async Task OldBinaryTerminalDuringAB3WarmingRoute_WritesTheSameBytesAsTheNewLegacyShadow_DuplicateNeverConflict()
    {
        // Rolling B1/B2-shaped terminal (8d47b5e5c gate: any v1 route, phase ignored) on an old
        // silo while a B3 source warms the terminal route at epoch 1: the old terminal writes the
        // warming versions; so does the B3 legacy shadow (both copy the warming route through the
        // same mapping); the B3 terminal only reports. Same bytes, duplicates in either order.
        var world = new MixedBinaryWorld();
        var routeCarrier = new ProjectionScopeState();
        var warming = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Warming);
        warming.WarmStartedVersion = 4;
        warming.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        var warmingStarted = new ProjectionScopeStatusRouteWarmingStartedEvent { Route = warming, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingStarted(routeCarrier, warmingStarted);
        PreviousBinaryProjectionScopeStatusTerminalWriter.IsActiveTerminalRoute(routeCarrier.StatusRoute).Should().BeTrue("the old gate ignores WARMING");
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(routeCarrier.StatusRoute).Should().BeTrue();

        var v4 = Publication(4, routeCarrier.StatusRoute, eventData: warmingStarted);
        (await world.NewBinaryLegacyWriteAsync(v4)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(v4)).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.PreviousBinaryTerminalWriteAsync(v4, WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.TerminalObserveAsync(v4)).Should().BeNull("the B3 terminal reports while warming");
        world.Terminal.CaughtUpReports.Select(static r => r.ObservedVersion).Should().Equal(4);

        var caughtUp = new ProjectionScopeStatusRouteCaughtUpEvent { RouteEpoch = 1, ObservedVersion = 4, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteCaughtUp(routeCarrier, caughtUp);
        var v5 = Publication(5, routeCarrier.StatusRoute, eventData: caughtUp);
        (await world.PreviousBinaryTerminalWriteAsync(v5, WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Applied, "old terminal first");
        (await world.NewBinaryLegacyWriteAsync(v5)).Should().Be(ProjectionWriteDisposition.Duplicate, "new legacy shadow second");
        await world.AssertStoreIsByteImageOfAsync(5, carriedRoute: routeCarrier.StatusRoute);

        // BLOCKED: the B3 legacy shadow stops; the B3 terminal writes; the old terminal (phase
        // ignored) writes the same bytes.
        var blocked = new ProjectionScopeStatusRouteBlockedEvent { RouteEpoch = 1, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteBlocked(routeCarrier, blocked);
        var v6 = Publication(6, routeCarrier.StatusRoute, eventData: blocked);
        (await world.NewBinaryLegacyWriteAsync(v6)).Should().BeNull();
        (await world.TerminalObserveAsync(v6)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.PreviousBinaryTerminalWriteAsync(v6)).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.PreviousBinaryTerminalWriteAsync(v6, WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Duplicate);

        world.AssertNoConflictFromTheCurrentStore();
        world.Journal.Writes.Should().NotContain(static w => w.Disposition == ProjectionWriteDisposition.Conflict);
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        await world.AssertStoreIsByteImageOfAsync(6, carriedRoute: routeCarrier.StatusRoute);
    }

    // ── 5. rolling forward from B0 through the phased cutover ──────────────────────────

    [Fact]
    public async Task RollingForwardFromB0_ThroughThePhasedCutover_NeverConflicts()
    {
        var world = new MixedBinaryWorld();

        // B0's legacy shadow owns v1..v3 (no route on the source).
        for (long version = 1; version <= 3; version++)
            (await world.OldBinaryWriteAsync(Publication(version, route: null))).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.OldBinaryWriteAsync(Publication(3, route: null))).Should().Be(ProjectionWriteDisposition.Duplicate, "a B0 redelivery of the current version");
        await world.AssertStoreIsByteImageOfAsync(3, carriedRoute: null);

        // The upgraded source activates with a fresh terminal admission and starts WARMING at
        // epoch 1 (ProjectionScopeGAgentBase.StatusRoute.cs StartWarmingAsync): the WarmingStarted
        // commit is v4, so WarmStartedVersion = 4. Routes evolve through the real appliers.
        var routeCarrier = new ProjectionScopeState();
        var warming = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Warming);
        warming.WarmStartedVersion = 4;
        warming.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
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

        // The source handles the report (HandleStatusWriterCaughtUpAsync): CaughtUp (v5), then
        // BLOCKED (v6), previous writer released (v7), ACTIVE (v8) — one source turn, four commits.
        var caughtUp = new ProjectionScopeStatusRouteCaughtUpEvent { RouteEpoch = 1, ObservedVersion = 4, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteCaughtUp(routeCarrier, caughtUp);
        routeCarrier.StatusRoute!.CaughtUpVersion.Should().Be(4);
        var v5 = Publication(5, routeCarrier.StatusRoute, eventData: caughtUp);
        (await world.NewBinaryLegacyWriteAsync(v5)).Should().Be(ProjectionWriteDisposition.Applied, "still warming: the legacy shadow writes");
        (await world.TerminalObserveAsync(v5)).Should().BeNull();
        world.Terminal.CaughtUpReports.Select(static r => r.ObservedVersion).Should().Equal(4, 5);
        (await world.OldBinaryWriteAsync(v5)).Should().Be(ProjectionWriteDisposition.Stale, "the B0 straggler at a version already held under epoch 1");

        var blocked = new ProjectionScopeStatusRouteBlockedEvent { RouteEpoch = 1, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteBlocked(routeCarrier, blocked);
        routeCarrier.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        var v6 = Publication(6, routeCarrier.StatusRoute, eventData: blocked);
        // v6 BLOCKED: the B3 legacy shadow skips (a hosted shadow releases itself); the terminal
        // writes (BLOCKED is a writing phase: the epoch-fenced takeover version). B0 never saw the
        // block and delivers v6 too: before the terminal it is applied then taken over, after it
        // it is stale — never a conflict.
        (await world.NewBinaryLegacyWriteAsync(v6)).Should().BeNull();
        (await world.OldBinaryWriteAsync(v6)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v6)).Should().Be(ProjectionWriteDisposition.Applied, "epoch-1 takeover of B0's epoch-0 v6");
        (await world.OldBinaryWriteAsync(v6)).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(v6, WriteEvaluator.OldBinaryPlain)).Should().Be(ProjectionWriteDisposition.Conflict, "B0's own log only");
        await world.AssertStoreIsByteImageOfAsync(6, carriedRoute: routeCarrier.StatusRoute);
        world.Terminal.CaughtUpReports.Should().HaveCount(2, "no report once the route left WARMING");

        var released = new ProjectionScopeStatusLegacyRouteReleasedEvent { RouteEpoch = 1, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(routeCarrier, released);
        routeCarrier.StatusRoute!.LegacyRouteReleased.Should().BeTrue();
        var v7 = Publication(7, routeCarrier.StatusRoute, eventData: released);
        (await world.TerminalObserveAsync(v7)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(v7)).Should().BeNull();

        var flipped = routeCarrier.StatusRoute.Clone();
        flipped.Phase = ProjectionScopeStatusRoutePhase.Active;
        flipped.FlipVersion = 8;
        flipped.LegacyRouteReleased = true;
        var activated = new ProjectionScopeStatusRouteActivatedEvent { Route = flipped, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteActivated(routeCarrier, activated);
        routeCarrier.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        var v8 = Publication(8, routeCarrier.StatusRoute, eventData: activated);
        (await world.TerminalObserveAsync(v8)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(v8)).Should().BeNull();
        (await world.TerminalObserveAsync(Publication(9, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Applied);

        // Delayed B0 deliveries after the flip: lower versions and the already-taken-over versions.
        (await world.OldBinaryWriteAsync(Publication(3, route: null))).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(v8)).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(Publication(9, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Stale);

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.Agent.State.PendingWrite.Should().BeNull();
        await world.AssertStoreIsByteImageOfAsync(9, carriedRoute: routeCarrier.StatusRoute);
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
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 6, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 6, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 6, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, WriteEvaluator.OldBinaryPlain, 6, ProjectionWriteDisposition.Conflict),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 7, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 8, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, WriteEvaluator.CurrentStore, 9, ProjectionWriteDisposition.Applied),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 3, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 8, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, WriteEvaluator.CurrentStore, 9, ProjectionWriteDisposition.Stale));
    }

    // ── 6. rollback on revocation through the phases ───────────────────────────────────

    [Fact]
    public async Task RollbackOnRevocation_ThroughThePhases_TerminalWritesWhileLegacyWarms_ThenReleases_NeverConflicts()
    {
        var world = new MixedBinaryWorld();

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

        // The fleet revokes the terminal admission: on activation the source starts WARMING the
        // legacy route at epoch 2 (MaintainActiveTerminalRouteAsync -> StartWarmingAsync with
        // BuildLegacyRoute(epoch + 1, Warming)); the commit is v9.
        var legacyWarming = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, ProjectionScopeStatusRoutePhase.Warming);
        legacyWarming.WarmStartedVersion = 9;
        legacyWarming.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        var warmingStarted = new ProjectionScopeStatusRouteWarmingStartedEvent { Route = legacyWarming, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteWarmingStarted(routeCarrier, warmingStarted);
        ProjectionScopeStatusRoutePolicy.IsLegacyRoute(routeCarrier.StatusRoute).Should().BeTrue();
        routeCarrier.StatusRoute!.RouteEpoch.Should().Be(2);
        var v9 = Publication(9, routeCarrier.StatusRoute, eventData: warmingStarted);

        // v9 legacy WARMING: the terminal keeps writing (under epoch 2 now); the B3 legacy
        // projector does not write yet. A hosted legacy shadow scope reports caught-up to the
        // source (ProjectionScopeGAgentBase.StatusRoute.cs ReconcileStatusRouteOnObservationAsync);
        // here the report is applied to the source directly.
        (await world.TerminalObserveAsync(v9)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.NewBinaryLegacyWriteAsync(v9)).Should().BeNull();
        world.Terminal.Agent.State.Released.Should().BeFalse();
        await world.AssertStoreIsByteImageOfAsync(9, carriedRoute: routeCarrier.StatusRoute);

        var caughtUp = new ProjectionScopeStatusRouteCaughtUpEvent { RouteEpoch = 2, ObservedVersion = 9, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteCaughtUp(routeCarrier, caughtUp);
        var v10 = Publication(10, routeCarrier.StatusRoute, eventData: caughtUp);
        (await world.TerminalObserveAsync(v10)).Should().Be(ProjectionWriteDisposition.Applied, "still warming: the terminal is the writer");
        (await world.NewBinaryLegacyWriteAsync(v10)).Should().BeNull();

        // v11 legacy BLOCKED: the terminal releases itself (no write); the legacy shadow writes
        // (BLOCKED is a writing phase for the legacy route).
        var blocked = new ProjectionScopeStatusRouteBlockedEvent { RouteEpoch = 2, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteBlocked(routeCarrier, blocked);
        var v11 = Publication(11, routeCarrier.StatusRoute, eventData: blocked);
        (await world.TerminalObserveAsync(v11)).Should().BeNull("the terminal releases itself on a blocked legacy route");
        world.Terminal.Agent.State.Released.Should().BeTrue();
        world.Terminal.EventSourcing.Committed.OfType<ProjectionScopeStatusTerminalReleasedEvent>().Should().ContainSingle();
        (await world.NewBinaryLegacyWriteAsync(v11)).Should().Be(ProjectionWriteDisposition.Applied);
        await world.AssertStoreIsByteImageOfAsync(11, carriedRoute: routeCarrier.StatusRoute);

        // v12 previous writer released (the source removes the terminal relay and dispatches
        // Release: idempotent, the terminal already released), v13 legacy ACTIVE.
        var released = new ProjectionScopeStatusLegacyRouteReleasedEvent { RouteEpoch = 2, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(routeCarrier, released);
        var v12 = Publication(12, routeCarrier.StatusRoute, eventData: released);
        (await world.NewBinaryLegacyWriteAsync(v12)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v12)).Should().BeNull("released, and a legacy route never names the terminal");

        var flipped = routeCarrier.StatusRoute!.Clone();
        flipped.Phase = ProjectionScopeStatusRoutePhase.Active;
        flipped.FlipVersion = 13;
        flipped.LegacyRouteReleased = true;
        var activated = new ProjectionScopeStatusRouteActivatedEvent { Route = flipped, OccurredAtUtc = Timestamp.FromDateTimeOffset(Now) };
        routeCarrier = ProjectionScopeStateApplier.ApplyStatusRouteActivated(routeCarrier, activated);
        routeCarrier.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        var v13 = Publication(13, routeCarrier.StatusRoute, eventData: activated);
        (await world.NewBinaryLegacyWriteAsync(v13)).Should().Be(ProjectionWriteDisposition.Applied);
        (await world.TerminalObserveAsync(v13)).Should().BeNull();
        world.Terminal.Agent.State.Released.Should().BeTrue("a legacy route in a writing phase never restarts the terminal");

        // Late redeliveries while draining: the released terminal ignores its old versions; the
        // legacy shadow's redelivery of the current version is a duplicate and of an older one
        // stale; a late delivery of a warming-phase version is skipped by the legacy projector; a
        // B0 old binary (if a rollback went all the way back) takes the document forward with a
        // higher version.
        (await world.TerminalObserveAsync(v10)).Should().BeNull();
        (await world.NewBinaryLegacyWriteAsync(v13)).Should().Be(ProjectionWriteDisposition.Duplicate);
        (await world.NewBinaryLegacyWriteAsync(v11)).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.NewBinaryLegacyWriteAsync(v9)).Should().BeNull();
        (await world.OldBinaryWriteAsync(Publication(13, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Stale);
        (await world.OldBinaryWriteAsync(Publication(14, routeCarrier.StatusRoute))).Should().Be(ProjectionWriteDisposition.Applied, "version wins over epoch");

        world.AssertNoConflictFromTheCurrentStore();
        world.Terminal.CaughtUpReports.Should().BeEmpty("the terminal reports only while a TERMINAL route warms");
        await world.AssertStoreIsByteImageOfAsync(14, carriedRoute: null);
        world.Journal.Writes.Select(static w => (w.Writer, w.Version, w.Disposition)).Should().Equal(
            (StatusWriter.Terminal, 8, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 9, ProjectionWriteDisposition.Applied),
            (StatusWriter.Terminal, 10, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 11, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 12, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 13, ProjectionWriteDisposition.Applied),
            (StatusWriter.NewBinaryLegacy, 13, ProjectionWriteDisposition.Duplicate),
            (StatusWriter.NewBinaryLegacy, 11, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, 13, ProjectionWriteDisposition.Stale),
            (StatusWriter.OldBinary, 14, ProjectionWriteDisposition.Applied));
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
            (await world.TerminalObserveAsync(Publication(version, route, "source-evt-3-different-fact")))
                .Should().Be(ProjectionWriteDisposition.Conflict);
            var pending = world.Terminal.Agent.State.PendingWrite;
            pending.Should().NotBeNull("the terminal records a rejected write as an explicit failure state");
            pending!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
            pending.Source.StateVersion.Should().Be(version);
            pending.Source.EventId.Should().Be("source-evt-3-different-fact");
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
        (await world.TerminalObserveAsync(Publication(version, divergedState)))
            .Should().Be(ProjectionWriteDisposition.Conflict, "same version, event id and epoch but different bytes is a different fact");

        world.Terminal.Agent.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        await world.AssertStoreIsByteImageOfAsync(version, carriedRoute: route);
    }

    // ── routes ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The route B1 (8d47b5e5c) and B2 (416e80f4a) committed on adoption: terminal contract,
    /// contract version 1 (their reader version), phase-less, released legacy shadow.
    /// </summary>
    private static ProjectionScopeStatusRoute V1TerminalRoute(long epoch) =>
        new()
        {
            ContractId = ProjectionScopeStatusGAgent.ContractId,
            ContractVersion = PreviousBinaryProjectionScopeStatusTerminalWriter.ReaderVersion,
            RouteEpoch = epoch,
            ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now),
            LegacyRouteReleased = true,
        };

    /// <summary>
    /// A route committed by B3's phased cutover: terminal contract at the current reader version
    /// (1, the same as B1/B2's) with a phase. It differs from <see cref="V1TerminalRoute"/> only by
    /// the phase field; B1/B2 terminal gates accept both.
    /// </summary>
    private static ProjectionScopeStatusRoute TerminalRoute(
        long epoch,
        ProjectionScopeStatusRoutePhase phase = ProjectionScopeStatusRoutePhase.Active)
    {
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(epoch, phase);
        route.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);
        route.LegacyRouteReleased = phase == ProjectionScopeStatusRoutePhase.Active;
        return route;
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
        private readonly ProjectionScopeStatusProjector _newBinaryLegacy;
        private readonly ProjectionScopeStatusMaterializationContext _legacyContext =
            new() { RootActorId = SourceScopeActorId };

        public MixedBinaryWorld()
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
            _newBinaryLegacy = new ProjectionScopeStatusProjector(
                Journal.DispatcherFor(StatusWriter.NewBinaryLegacy, WriteEvaluator.CurrentStore),
                _clock);
            Terminal = TerminalAgentHarness.Build(Journal.DispatcherFor(StatusWriter.Terminal, WriteEvaluator.CurrentStore), _clock);
        }

        public StatusStoreJournal Journal { get; } = new();
        public TerminalAgentHarness Terminal { get; }

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

        /// <summary>Caught-up reports the warming terminal sent to its source scope.</summary>
        public IReadOnlyList<ProjectionScopeStatusWriterCaughtUpEvent> CaughtUpReports =>
            Outbox.Sent
                .Where(static s => s.TargetActorId == SourceScopeActorId)
                .Select(static s => s.Message)
                .OfType<ProjectionScopeStatusWriterCaughtUpEvent>()
                .ToList();

        public static TerminalAgentHarness Build(
            IProjectionWriteDispatcher<ProjectionScopeStatusDocument> dispatcher,
            IProjectionClock clock)
        {
            var outbox = new RecordingEventPublisher();
            var agent = new ProjectionScopeStatusGAgent { EventPublisher = outbox };
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
            return new TerminalAgentHarness { Agent = agent, EventSourcing = eventSourcing, Outbox = outbox };
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
    /// <c>RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion</c> of 8d47b5e5c
    /// and 416e80f4a (and of B3, which deliberately keeps 1 so every route it commits stays
    /// serviceable by these binaries after a rollback).
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
