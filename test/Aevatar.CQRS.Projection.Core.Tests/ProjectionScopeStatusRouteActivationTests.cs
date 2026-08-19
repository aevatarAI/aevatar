using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Type = System.Type;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// Source-scope ownership of the status write route (#3476), phased cutover edition: a durable
/// projection scope actor ensures its own legacy status shadow, warms the terminal materializer
/// once the fleet admits the terminal contract, blocks and flips only after the new writer has
/// reported caught-up, releases the previous writer, fences the route by epoch, resumes the
/// cutover from any committed phase on restart, and rolls back to the legacy writer through the
/// same phases when the fleet revokes the gate. Also covers the legacy shadow's observation-side
/// behaviour and the rolling window in which two status writers overlap on one source.
/// </summary>
public sealed class ProjectionScopeStatusRouteActivationTests
{
    private const string RootActorId = "root-actor";
    private const string ProjectionKind = "test-kind";
    private const string TestScopeAgentKind = "projection.materialization-scope.test-context";

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    private static readonly string SourceScopeActorId = ProjectionScopeActorId.Build(
        new ProjectionRuntimeScopeKey(RootActorId, ProjectionKind, ProjectionRuntimeMode.DurableMaterialization));

    private static readonly string TerminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(SourceScopeActorId);
    private static readonly string LegacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(SourceScopeActorId);

    /// <summary>
    /// The legacy status shadow kind exactly as <see cref="ProjectionScopeStatusRuntimeRegistration"/>
    /// registers it; the source scope resolves it through the kind registry to ensure its shadow.
    /// </summary>
    private static readonly string LegacyStatusShadowKind =
        ProjectionScopeAgentRegistration
            .Create<ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>>()
            .Kind;

    [Fact]
    public void LegacyStatusShadowKind_IsTheRegisteredMaterializationScopeKindOfTheStatusContext()
    {
        LegacyStatusShadowKind.Should().Be(
            "projection.materialization-scope.projection-scope-status-materialization-context");
    }

    // ── A. no grant: the legacy shadow is the writer, ensured by the source itself ──────

    public enum NoGrantReason
    {
        AdmissionMissing,
        MembershipMismatch,
        GateRevoked,
        WrongCapability,
        AdmissionExpired,
    }

    [Theory]
    [InlineData(NoGrantReason.AdmissionMissing)]
    [InlineData(NoGrantReason.MembershipMismatch)]
    [InlineData(NoGrantReason.GateRevoked)]
    [InlineData(NoGrantReason.WrongCapability)]
    [InlineData(NoGrantReason.AdmissionExpired)]
    public async Task HandleEnsureAsync_WithoutFreshGrant_EnsuresLegacyShadowBeforeItsOwnRelayAndSchedulesAdoptionRetry(
        NoGrantReason reason)
    {
        var harness = BuildNoGrantHarness(reason, registerCallbackScheduler: true);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        // The source owns the legacy shadow: created by its registered kind when missing, then
        // the lifecycle command into its inbox — both before this scope's own observation relay
        // (the activation evidence) becomes visible. No route is decided; a durable adoption
        // retry is scheduled because the gate may open later.
        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            ActorCreate(LegacyStatusShadowKind, LegacyActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RetryTimeout(TestScopeAgent.RetryDelays[0]),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ]);
        harness.Runtime.CreatedByKind.Should().ContainSingle()
            .Which.Should().Be((LegacyStatusShadowKind, LegacyActorId));
        var legacyEnsure = harness.DispatchPort.Dispatched.Should().ContainSingle().Subject;
        legacyEnsure.actorId.Should().Be(LegacyActorId);
        legacyEnsure.command.Payload!.Unpack<EnsureProjectionScopeCommand>().Should().Be(
            new EnsureProjectionScopeCommand
            {
                RootActorId = SourceScopeActorId,
                ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
        legacyEnsure.command.Route.PublisherActorId.Should().Be(SourceScopeActorId);
        legacyEnsure.command.Route.Direct.TargetActorId.Should().Be(LegacyActorId);
        legacyEnsure.command.Propagation.CorrelationId.Should().Be(LegacyActorId);

        var timeout = harness.Callbacks!.Timeouts.Should().ContainSingle().Subject;
        timeout.ActorId.Should().Be(SourceScopeActorId);
        timeout.CallbackId.Should().Be(TestScopeAgent.RetryCallbackId);
        timeout.DueTime.Should().Be(TimeSpan.FromSeconds(30));
        timeout.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt.Should().Be(1);

        harness.EventSourcing.Committed.Select(static evt => evt.Descriptor.Name).Should().Equal(
            ProjectionScopeStartedEvent.Descriptor.Name,
            ProjectionObservationAttachmentUpdatedEvent.Descriptor.Name);
        harness.Streams.Relays.Keys.Should().Equal((RootActorId, SourceScopeActorId));
        harness.Streams.Removed.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeNull();
        harness.Agent.State.Active.Should().BeTrue();
        harness.Agent.State.ObservationAttached.Should().BeTrue();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithoutFreshGrant_WhenLegacyShadowExists_EnsuresItWithoutRecreating()
    {
        var harness = SourceScopeHarness.Build(admission: null);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ], "the legacy ensure is idempotent: no re-creation, the lifecycle command is still dispatched");
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithoutFreshGrantAndWithoutCallbackScheduler_EnsuresLegacyShadowWithoutRetry()
    {
        var harness = SourceScopeHarness.Build(admission: null, registerCallbackScheduler: false);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            ActorCreate(LegacyStatusShadowKind, LegacyActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ]);
        harness.Callbacks.Should().BeNull();
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    [Fact]
    public async Task HandleRetryStatusRouteAdoptionAsync_WhileGateStaysClosed_ReEnsuresLegacyAndBacksOffToLastDelayForever()
    {
        // A fleet gate can open long after silo boot (rolling upgrades, late silos); a scope
        // that stopped retrying would stay on the legacy route forever. Past the last delay
        // the schedule keeps that cadence and the attempt counter keeps increasing; every
        // attempt re-ensures the legacy shadow (idempotent) before re-reading the admission.
        const int attemptsBeyondSchedule = 3;
        var attemptsToDrive = TestScopeAgent.RetryDelays.Length + attemptsBeyondSchedule; // 8
        var harness = SourceScopeHarness.Build(admission: null, registerCallbackScheduler: true);
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        harness.Journal.Clear();

        for (var delivered = 0; delivered < attemptsToDrive; delivered++)
        {
            harness.Callbacks!.Timeouts.Should().HaveCount(delivered + 1, "every attempt schedules exactly one next attempt");
            var retry = harness.Callbacks.Timeouts[delivered].TriggerEnvelope.Payload!
                .Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>();
            retry.Attempt.Should().Be(delivered + 1);
            await harness.Agent.HandleRetryStatusRouteAdoptionAsync(retry);
        }

        // Attempt n (0 = the ensure itself) schedules attempt n+1 with delays[min(n, last)].
        var lastDelay = TestScopeAgent.RetryDelays[^1];
        lastDelay.Should().Be(TimeSpan.FromMinutes(8));
        var expectedDelays = TestScopeAgent.RetryDelays
            .Concat(Enumerable.Repeat(lastDelay, attemptsBeyondSchedule + 1))
            .ToArray();
        harness.Callbacks!.Timeouts.Should().HaveCount(attemptsToDrive + 1, "the last driven attempt scheduled yet another one");
        harness.Callbacks.Timeouts.Select(static t => t.DueTime).Should().Equal(expectedDelays);
        harness.Callbacks.Timeouts.Select(static t =>
                t.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt)
            .Should().Equal(Enumerable.Range(1, attemptsToDrive + 1));
        harness.Callbacks.Timeouts.Should().OnlyContain(t => t.CallbackId == TestScopeAgent.RetryCallbackId);
        harness.Journal.Should().Equal(
            Enumerable.Range(1, attemptsToDrive).SelectMany(attempt => new[]
            {
                Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
                RetryTimeout(TestScopeAgent.RetryDelays[Math.Min(attempt, TestScopeAgent.RetryDelays.Length - 1)]),
            }));
        harness.Runtime.CreatedByKind.Should().ContainSingle("the shadow was created by the ensure; retries only re-ensure it");
        harness.Agent.State.StatusRoute.Should().BeNull();
        harness.EventSourcing.Committed.Should().NotContain(evt => evt is ProjectionScopeStatusRouteWarmingStartedEvent);
    }

    [Fact]
    public async Task HandleRetryStatusRouteAdoptionAsync_WhenGateOpenedMeanwhile_StartsWarmingAndSchedulesTheWarmingContinuation()
    {
        var harness = SourceScopeHarness.Build(admission: null, registerCallbackScheduler: true);
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        var retry = harness.Callbacks!.Timeouts.Single().TriggerEnvelope.Payload!
            .Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>();
        retry.Attempt.Should().Be(1);
        harness.Fleet!.Admission = CreateAdmission();
        harness.Journal.Clear();

        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(retry);

        // The adoption retry chain ends here; the warming start schedules exactly one NEW
        // continuation of its own (the warming chain restarts at attempt 1 / 30s).
        harness.Journal.Should().Equal(
        [
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            ActorCreate(ProjectionScopeStatusGAgent.AgentKind, TerminalActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusRouteWarmingStartedEvent.Descriptor),
            RetryTimeout(TimeSpan.FromSeconds(30)),
        ]);
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        harness.Agent.State.StatusRoute.RouteEpoch.Should().Be(1);
        harness.Callbacks.Timeouts.Should().HaveCount(2, "the ensure scheduled the adoption retry; warming scheduled its own continuation");
        var continuation = harness.Callbacks.Timeouts[1];
        continuation.CallbackId.Should().Be(TestScopeAgent.RetryCallbackId);
        continuation.DueTime.Should().Be(TimeSpan.FromSeconds(30));
        continuation.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt.Should().Be(1);
    }

    [Fact]
    public async Task HandleRetryStatusRouteAdoptionAsync_WhenGateOpensBeyondTheDelaySchedule_StartsWarming()
    {
        const int grantAppearsAtAttempt = 7;
        var harness = SourceScopeHarness.Build(admission: null, registerCallbackScheduler: true);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        for (var delivered = 0; delivered < grantAppearsAtAttempt - 1; delivered++)
        {
            await harness.Agent.HandleRetryStatusRouteAdoptionAsync(
                harness.Callbacks!.Timeouts[delivered].TriggerEnvelope.Payload!
                    .Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>());
        }

        var retry = harness.Callbacks!.Timeouts[grantAppearsAtAttempt - 1].TriggerEnvelope.Payload!
            .Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>();
        retry.Attempt.Should().Be(grantAppearsAtAttempt);
        harness.Callbacks.Timeouts.Should().HaveCount(grantAppearsAtAttempt);
        harness.Fleet!.Admission = CreateAdmission();

        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(retry);

        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteWarmingStartedEvent>().Should().ContainSingle()
            .Which.Route.RouteEpoch.Should().Be(1);
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId));
        harness.DispatchPort.Dispatched.Select(static x => x.actorId).Should().Equal(
            Enumerable.Repeat(LegacyActorId, grantAppearsAtAttempt + 1).Append(TerminalActorId),
            "every ensure/retry re-ensured the legacy shadow; the grant additionally ensured the terminal");
        harness.Callbacks.Timeouts.Should().HaveCount(
            grantAppearsAtAttempt + 1,
            "the adoption retry chain stops; the warming start schedules exactly one new continuation");
        var continuation = harness.Callbacks.Timeouts[^1];
        continuation.DueTime.Should().Be(TimeSpan.FromSeconds(30), "the warming continuation restarts the backoff");
        continuation.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt.Should().Be(1);
    }

    [Fact]
    public async Task HandleEnsureAsync_WithoutRuntimePorts_DoesNothingAboutTheStatusRoute()
    {
        var harness = SourceScopeHarness.Build(
            admission: CreateAdmission(),
            registerRuntimeServices: false,
            registerCallbackScheduler: true);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ], "without actor runtime ports the scope can neither ensure a writer nor retry");
        harness.Callbacks!.Timeouts.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithoutFleetReaders_EnsuresLegacyShadowButSchedulesNoRetry()
    {
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, registerCallbackScheduler: true);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            ActorCreate(LegacyStatusShadowKind, LegacyActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ], "a host without fleet readers can never adopt here, so no retry is scheduled");
        harness.Callbacks!.Timeouts.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithoutLegacyStatusKindRegistered_SkipsLegacyEnsureButStillSchedulesRetry()
    {
        // Hosts without the status runtime registered have no status mirror at all.
        var harness = SourceScopeHarness.Build(
            admission: null,
            registerCallbackScheduler: true,
            registerLegacyStatusShadowKind: false);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            RetryTimeout(TestScopeAgent.RetryDelays[0]),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ]);
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
    }

    // ── B. fresh grant: warming starts, the legacy shadow keeps writing ────────────────

    [Fact]
    public async Task HandleEnsureAsync_WithFreshGrant_StartsWarmingTerminalRouteWhileLegacyShadowStaysTheWriter()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission(), registerCallbackScheduler: true);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        // Ordering contract: the legacy shadow is ensured (it is still the writer); the terminal
        // writer is installed (relay written, materializer created and ensured) BEFORE the
        // warming route is committed, so the publication of the warming commit itself is the
        // first routed envelope the new writer observes; then the durable warming continuation
        // is scheduled — all before this scope's own observation relay appears. The legacy
        // shadow is neither released nor its relay removed: nothing flips before the new writer
        // caught up.
        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            ActorCreate(LegacyStatusShadowKind, LegacyActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            ActorCreate(ProjectionScopeStatusGAgent.AgentKind, TerminalActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusRouteWarmingStartedEvent.Descriptor),
            RetryTimeout(TimeSpan.FromSeconds(30)),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ]);
        var continuation = harness.Callbacks!.Timeouts.Should().ContainSingle().Subject;
        continuation.ActorId.Should().Be(SourceScopeActorId);
        continuation.CallbackId.Should().Be(TestScopeAgent.RetryCallbackId);
        continuation.DueTime.Should().Be(TimeSpan.FromSeconds(30));
        continuation.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt.Should().Be(1);

        var terminalRelay = harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId)).WhoseValue;
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                terminalRelay,
                SourceScopeActorId,
                TerminalActorId,
                ProjectionScopeStatusGAgent.AgentKind)
            .Should().BeTrue();
        terminalRelay.ActivationGeneration.Should().Be(1);

        var terminalEnsure = harness.DispatchPort.Dispatched
            .Should().ContainSingle(x => x.actorId == TerminalActorId).Subject;
        terminalEnsure.command.Payload!.Unpack<EnsureProjectionScopeCommand>().Should().Be(
            new EnsureProjectionScopeCommand
            {
                RootActorId = SourceScopeActorId,
                ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
        terminalEnsure.command.Route.PublisherActorId.Should().Be(SourceScopeActorId);
        terminalEnsure.command.Route.Direct.TargetActorId.Should().Be(TerminalActorId);
        terminalEnsure.command.Propagation.CorrelationId.Should().Be(TerminalActorId);

        // The warming route: epoch 1, the terminal contract (reader v1), phase WARMING, the
        // warm-started version is the version of the WarmingStarted commit itself (Started = 1,
        // WarmingStarted = 2), and the activation proof is the fleet grant.
        var warming = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteWarmingStartedEvent>()
            .Should().ContainSingle().Subject;
        warming.OccurredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(Now));
        warming.Route.RouteEpoch.Should().Be(1);
        warming.Route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        warming.Route.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
        warming.Route.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        warming.Route.ContractVersion.Should().Be(1, "the terminal reader contract is pinned at v1 for rollback safety");
        warming.Route.ContractVersion.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);
        warming.Route.ContractVersion.Should().Be(ProjectionScopeStatusGAgent.ContractVersion);
        warming.Route.WarmStartedVersion.Should().Be(2);
        warming.Route.CaughtUpVersion.Should().Be(0);
        warming.Route.FlipVersion.Should().Be(0);
        warming.Route.LegacyRouteReleased.Should().BeFalse();
        warming.Route.ActivatedAtUtc.Should().Be(Timestamp.FromDateTimeOffset(Now));
        warming.Route.ActivationProof.Should().Be(BuildExpectedActivationProof());

        harness.EventSourcing.Committed.Should().NotContain(evt =>
            evt is ProjectionScopeStatusRouteCaughtUpEvent ||
            evt is ProjectionScopeStatusRouteBlockedEvent ||
            evt is ProjectionScopeStatusLegacyRouteReleasedEvent ||
            evt is ProjectionScopeStatusRouteActivatedEvent);
        harness.Streams.Removed.Should().BeEmpty("the legacy shadow is not released while warming");
        harness.DispatchPort.Dispatched.Select(static x => x.actorId).Should().Equal(LegacyActorId, TerminalActorId);
        harness.DispatchPort.Dispatched.Should().OnlyContain(x => x.command.Payload!.Is(EnsureProjectionScopeCommand.Descriptor));

        var route = harness.Agent.State.StatusRoute;
        route.Should().NotBeNull();
        route!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        route.RouteEpoch.Should().Be(1);
        route.WarmStartedVersion.Should().Be(2);
        route.CaughtUpVersion.Should().Be(0);
        route.FlipVersion.Should().Be(0);
        route.LegacyRouteReleased.Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                route, ProjectionScopeStatusGAgent.ContractId, ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route, ProjectionScopeStatusGAgent.ContractId, ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(route).Should().BeTrue("the legacy shadow writes until the flip");
        ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(route).Should().BeFalse();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithFreshGrant_WhenWriterActorsAlreadyExist_EnsuresWithoutRecreatingThem()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission());
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Select(static x => x.actorId).Should().Equal(LegacyActorId, TerminalActorId);
        harness.DispatchPort.Dispatched.Should().OnlyContain(x => x.command.Payload!.Is(EnsureProjectionScopeCommand.Descriptor));
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
    }

    [Fact]
    public async Task ActivationPath_WithFreshGrant_StartsWarmingBeforeAssertingObservationRelay()
    {
        // The activation path mirrors the cold ensure path: legacy ensure, warming route,
        // terminal relay/materializer — all before this scope's own observation relay (the
        // activation evidence every activation service reads) is re-asserted.
        var harness = SourceScopeHarness.Build(
            admission: CreateAdmission(),
            registerCallbackScheduler: true,
            initialState: BuildActiveSourceState());
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);

        await harness.Agent.ActivateForTestAsync();

        harness.Journal.Should().Equal(
        [
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            ActorCreate(ProjectionScopeStatusGAgent.AgentKind, TerminalActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusRouteWarmingStartedEvent.Descriptor),
            RetryTimeout(TimeSpan.FromSeconds(30)),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.Callbacks!.Timeouts.Should().ContainSingle()
            .Which.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt.Should().Be(1);
        harness.EventSourcing.Committed.Select(static evt => evt.Descriptor.Name).Should().Equal(
            ProjectionScopeStatusRouteWarmingStartedEvent.Descriptor.Name);
        harness.Streams.Relays.Keys.Should().BeEquivalentTo(
        [
            (SourceScopeActorId, TerminalActorId),
            (RootActorId, SourceScopeActorId),
        ]);
        var route = harness.Agent.State.StatusRoute!;
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        route.RouteEpoch.Should().Be(1);
        route.WarmStartedVersion.Should().Be(1, "the WarmingStarted commit is this scope's first event");
        route.LegacyRouteReleased.Should().BeFalse();
    }

    // ── C. the caught-up report drives block → release → flip ─────────────────────────

    [Fact]
    public async Task HandleStatusWriterCaughtUpAsync_AtWarmStartedVersion_BlocksReleasesLegacyAndFlipsInOrder()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission(), registerCallbackScheduler: true);
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        harness.Callbacks!.Timeouts.Should().ContainSingle("warming scheduled its continuation");
        var warmStartedVersion = harness.Agent.State.StatusRoute!.WarmStartedVersion;
        warmStartedVersion.Should().Be(2);
        harness.Journal.Clear();

        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: warmStartedVersion));

        // CaughtUp → Blocked → (legacy relay removed, Release dispatched, release recorded)
        // → Activated: each a committed fact of this turn, in this order.
        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStatusRouteCaughtUpEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteBlockedEvent.Descriptor),
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Dispatch(ReleaseProjectionScopeCommand.Descriptor, LegacyActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
        ]);

        var caughtUp = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteCaughtUpEvent>().Should().ContainSingle().Subject;
        caughtUp.RouteEpoch.Should().Be(1);
        caughtUp.ObservedVersion.Should().Be(warmStartedVersion);
        caughtUp.OccurredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(Now));
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteBlockedEvent>().Should().ContainSingle()
            .Which.RouteEpoch.Should().Be(1);
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusLegacyRouteReleasedEvent>().Should().ContainSingle()
            .Which.RouteEpoch.Should().Be(1);

        var legacyRelease = harness.DispatchPort.Dispatched
            .Should().ContainSingle(x => x.command.Payload!.Is(ReleaseProjectionScopeCommand.Descriptor)).Subject;
        legacyRelease.actorId.Should().Be(LegacyActorId);
        legacyRelease.command.Payload!.Unpack<ReleaseProjectionScopeCommand>().Should().Be(
            new ReleaseProjectionScopeCommand
            {
                RootActorId = SourceScopeActorId,
                ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
        legacyRelease.command.Route.Direct.TargetActorId.Should().Be(LegacyActorId);

        var activated = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Should().ContainSingle().Subject;
        activated.OccurredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(Now));
        activated.Route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        activated.Route.RouteEpoch.Should().Be(1);
        activated.Route.LegacyRouteReleased.Should().BeTrue();
        activated.Route.WarmStartedVersion.Should().Be(warmStartedVersion);
        activated.Route.CaughtUpVersion.Should().Be(warmStartedVersion);
        // Started 1, WarmingStarted 2, Attachment 3, CaughtUp 4, Blocked 5, Released 6, Activated 7.
        harness.EventSourcing.CurrentVersion.Should().Be(7);
        activated.Route.FlipVersion.Should().Be(7, "the flip version is the version of the Activated commit itself");
        activated.Route.ActivationProof.Should().Be(BuildExpectedActivationProof());

        var route = harness.Agent.State.StatusRoute!;
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        route.RouteEpoch.Should().Be(1);
        route.FlipVersion.Should().Be(7);
        route.CaughtUpVersion.Should().Be(warmStartedVersion);
        route.LegacyRouteReleased.Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route, ProjectionScopeStatusGAgent.ContractId, ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(route).Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(route).Should().BeTrue();
        harness.Streams.Relays.Keys.Should().BeEquivalentTo(
        [
            (SourceScopeActorId, TerminalActorId),
            (RootActorId, SourceScopeActorId),
        ], "the legacy relay is gone, the terminal relay stays");
        harness.Callbacks.Timeouts.Should().ContainSingle("the flip schedules no further continuation");
        harness.Journal.Clear();

        // A duplicate report after the flip is ignored: the route is no longer warming.
        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: warmStartedVersion));
        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: 99));

        harness.Journal.Should().BeEmpty();
        harness.EventSourcing.CurrentVersion.Should().Be(7);
    }

    [Fact]
    public async Task HandleStatusWriterCaughtUpAsync_WhenLegacyActorDoesNotExist_ReleasesWithoutDispatchingToIt()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission(), initialState: BuildActiveSourceState());
        await harness.Agent.ActivateForTestAsync();
        harness.Runtime.ExistingActorIds.Remove(LegacyActorId); // the shadow vanished between warming and caught-up
        harness.Journal.Clear();

        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: 1));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStatusRouteCaughtUpEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteBlockedEvent.Descriptor),
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
        ], "no Release is sent to a legacy actor that does not exist; the release is still recorded");
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeTrue();
    }

    [Fact]
    public async Task HandleStatusWriterCaughtUpAsync_BelowWarmStartedVersion_RecordsProgressWithoutBlocking()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission());
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        var warmStartedVersion = harness.Agent.State.StatusRoute!.WarmStartedVersion;
        harness.Journal.Clear();

        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: warmStartedVersion - 1));

        harness.Journal.Should().Equal([Commit(ProjectionScopeStatusRouteCaughtUpEvent.Descriptor)]);
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteCaughtUpEvent>().Should().ContainSingle()
            .Which.ObservedVersion.Should().Be(warmStartedVersion - 1);
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        harness.Agent.State.StatusRoute.CaughtUpVersion.Should().Be(warmStartedVersion - 1);
        harness.Streams.Removed.Should().BeEmpty();
        harness.Journal.Clear();

        // Not higher than the recorded progress: nothing is committed, nothing flips.
        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: warmStartedVersion - 1));
        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: 0));

        harness.Journal.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);

        // Reaching the warm-started version completes the cutover.
        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: warmStartedVersion));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStatusRouteCaughtUpEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteBlockedEvent.Descriptor),
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Dispatch(ReleaseProjectionScopeCommand.Descriptor, LegacyActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
        ]);
        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
    }

    public enum IgnoredReportReason
    {
        WrongEpoch,
        WrongSource,
        NoRoute,
    }

    [Theory]
    [InlineData(IgnoredReportReason.WrongEpoch)]
    [InlineData(IgnoredReportReason.WrongSource)]
    [InlineData(IgnoredReportReason.NoRoute)]
    public async Task HandleStatusWriterCaughtUpAsync_ForWrongEpochSourceOrWithoutWarmingRoute_IsIgnored(IgnoredReportReason reason)
    {
        var harness = SourceScopeHarness.Build(admission: reason == IgnoredReportReason.NoRoute ? null : CreateAdmission());
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        var committedBefore = harness.EventSourcing.Committed.Count;
        harness.Journal.Clear();
        var report = reason switch
        {
            IgnoredReportReason.WrongEpoch => BuildCaughtUpReport(routeEpoch: 2, observedVersion: 50),
            IgnoredReportReason.WrongSource => BuildCaughtUpReport(routeEpoch: 1, observedVersion: 50, sourceScopeActorId: "some-other-scope"),
            IgnoredReportReason.NoRoute => BuildCaughtUpReport(routeEpoch: 1, observedVersion: 50),
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

        await harness.Agent.HandleStatusWriterCaughtUpAsync(report);

        harness.Journal.Should().BeEmpty();
        harness.EventSourcing.Committed.Should().HaveCount(committedBefore);
        if (reason == IgnoredReportReason.NoRoute)
            harness.Agent.State.StatusRoute.Should().BeNull();
        else
            harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
    }

    // ── D. restart between every phase resumes the cutover on activation / ensure ──────

    public enum AdoptedScopePath
    {
        Activation,
        Ensure,
    }

    [Theory]
    [InlineData(AdoptedScopePath.Activation)]
    [InlineData(AdoptedScopePath.Ensure)]
    public async Task RestartWhileWarmingNotCaughtUp_ReinstallsWarmingWriterProbesAndSchedulesContinuationWithoutFlipping(AdoptedScopePath path)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 5;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, registerCallbackScheduler: true, initialState: state);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();

        await RunAdoptedScopePathAsync(harness, path);

        // The warming writer is re-installed (relay, actor, lifecycle command), a probe is
        // committed so the relay carries a publication for an otherwise idle source, and the
        // durable continuation is scheduled (attempt 0 → next attempt 1 after 30s); nothing
        // flips and the legacy writer is untouched.
        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            ActorCreate(ProjectionScopeStatusGAgent.AgentKind, TerminalActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusRouteWarmingProbedEvent.Descriptor),
            RetryTimeout(TimeSpan.FromSeconds(30)),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        var continuation = harness.Callbacks!.Timeouts.Should().ContainSingle().Subject;
        continuation.CallbackId.Should().Be(TestScopeAgent.RetryCallbackId);
        continuation.DueTime.Should().Be(TimeSpan.FromSeconds(30));
        continuation.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt.Should().Be(1);
        var probed = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteWarmingProbedEvent>().Should().ContainSingle().Subject;
        probed.RouteEpoch.Should().Be(1);
        probed.OccurredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(Now));
        harness.EventSourcing.Committed.Should().HaveCount(1, "the probe is the only committed fact");
        harness.Streams.Relays.Keys.Should().BeEquivalentTo(
        [
            (SourceScopeActorId, LegacyActorId),
            (SourceScopeActorId, TerminalActorId),
            (RootActorId, SourceScopeActorId),
        ]);
        harness.DispatchPort.Dispatched.Should().NotContain(x => x.command.Payload!.Is(ReleaseProjectionScopeCommand.Descriptor));
        var route = harness.Agent.State.StatusRoute!;
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        route.RouteEpoch.Should().Be(1);
        route.WarmStartedVersion.Should().Be(5);
        route.LegacyRouteReleased.Should().BeFalse();
    }

    [Theory]
    [InlineData(AdoptedScopePath.Activation)]
    [InlineData(AdoptedScopePath.Ensure)]
    public async Task RestartWhileWarmingCaughtUp_BlocksReleasesAndFlips(AdoptedScopePath path)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 5;
        state.StatusRoute.CaughtUpVersion = 5;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, registerCallbackScheduler: true, initialState: state);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();

        await RunAdoptedScopePathAsync(harness, path);

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusRouteBlockedEvent.Descriptor),
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Dispatch(ReleaseProjectionScopeCommand.Descriptor, LegacyActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.Callbacks!.Timeouts.Should().BeEmpty("a completed cutover schedules no warming continuation");
        var activated = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Single();
        activated.Route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        activated.Route.FlipVersion.Should().Be(3, "Blocked 1, Released 2, Activated 3");
        activated.Route.LegacyRouteReleased.Should().BeTrue();
        harness.Streams.Relays.Keys.Should().BeEquivalentTo(
        [
            (SourceScopeActorId, TerminalActorId),
            (RootActorId, SourceScopeActorId),
        ]);
        var route = harness.Agent.State.StatusRoute!;
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        route.FlipVersion.Should().Be(3);
        route.LegacyRouteReleased.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRetryStatusRouteAdoptionAsync_WhileWarmingNotCaughtUp_ReprobesAndReschedulesWithBackedOffDelay()
    {
        // The fired warming continuation re-installs the writer, commits another probe (a fresh
        // publication through the relay) and re-schedules itself with the attempt-indexed delay;
        // the last delay repeats until the writer catches up. Nothing flips.
        const int attemptsToDrive = 7;
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 5;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, registerCallbackScheduler: true, initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();

        for (var attempt = 1; attempt <= attemptsToDrive; attempt++)
        {
            await harness.Agent.HandleRetryStatusRouteAdoptionAsync(
                new RetryProjectionScopeStatusRouteAdoptionCommand { Attempt = attempt });
        }

        harness.Journal.Should().Equal(
            Enumerable.Range(1, attemptsToDrive).SelectMany(attempt => new[]
            {
                RelayUpsert(SourceScopeActorId, TerminalActorId),
                Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
                Commit(ProjectionScopeStatusRouteWarmingProbedEvent.Descriptor),
                RetryTimeout(TestScopeAgent.RetryDelays[Math.Min(attempt, TestScopeAgent.RetryDelays.Length - 1)]),
            }));
        harness.Callbacks!.Timeouts.Select(static t => t.DueTime).Should().Equal(
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(8));
        harness.Callbacks.Timeouts.Select(static t =>
                t.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt)
            .Should().Equal(Enumerable.Range(2, attemptsToDrive));
        harness.Callbacks.Timeouts.Should().OnlyContain(t => t.CallbackId == TestScopeAgent.RetryCallbackId);
        harness.EventSourcing.Committed.Should().HaveCount(attemptsToDrive);
        harness.EventSourcing.Committed.Should().OnlyContain(evt => evt is ProjectionScopeStatusRouteWarmingProbedEvent);
        harness.Streams.Removed.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().OnlyContain(x => x.actorId == TerminalActorId);
        var route = harness.Agent.State.StatusRoute!;
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        route.RouteEpoch.Should().Be(1);
        route.CaughtUpVersion.Should().Be(0);
    }

    [Fact]
    public async Task HandleRetryStatusRouteAdoptionAsync_WhileWarmingCaughtUp_CompletesCutoverAndSchedulesNothingMore()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 5;
        state.StatusRoute.CaughtUpVersion = 5;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, registerCallbackScheduler: true, initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();

        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(
            new RetryProjectionScopeStatusRouteAdoptionCommand { Attempt = 3 });

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusRouteBlockedEvent.Descriptor),
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Dispatch(ReleaseProjectionScopeCommand.Descriptor, LegacyActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
        ]);
        harness.Callbacks!.Timeouts.Should().BeEmpty("the cutover completed; the continuation chain ends");
        var route = harness.Agent.State.StatusRoute!;
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        route.LegacyRouteReleased.Should().BeTrue();
        route.FlipVersion.Should().Be(3);
    }

    [Fact]
    public async Task HandleRetryStatusRouteAdoptionAsync_AfterTheFlip_OnlyMaintainsTheActiveTerminalRoute()
    {
        // The warming continuation scheduled at warming start fires after the writer already
        // caught up and the route flipped: it is a plain maintenance pass (no event, no retry).
        var harness = SourceScopeHarness.Build(admission: CreateAdmission(), registerCallbackScheduler: true);
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        await harness.Agent.HandleStatusWriterCaughtUpAsync(
            BuildCaughtUpReport(routeEpoch: 1, observedVersion: harness.Agent.State.StatusRoute!.WarmStartedVersion));
        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        var continuation = harness.Callbacks!.Timeouts.Should().ContainSingle().Subject;
        var committedBefore = harness.EventSourcing.Committed.Count;
        harness.Journal.Clear();

        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(
            continuation.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>());

        harness.Journal.Should().Equal([RelayUpsert(SourceScopeActorId, TerminalActorId)]);
        harness.EventSourcing.Committed.Should().HaveCount(committedBefore);
        harness.Callbacks.Timeouts.Should().ContainSingle("an active route schedules nothing");
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
    }

    [Theory]
    [InlineData(AdoptedScopePath.Activation)]
    [InlineData(AdoptedScopePath.Ensure)]
    public async Task RestartWhileBlockedBeforeRelease_ReleasesPreviousWriterAndFlips(AdoptedScopePath path)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.WarmStartedVersion = 5;
        state.StatusRoute.CaughtUpVersion = 6;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();

        await RunAdoptedScopePathAsync(harness, path);

        harness.Journal.Should().Equal(
        [
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Dispatch(ReleaseProjectionScopeCommand.Descriptor, LegacyActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusLegacyRouteReleasedEvent>().Single().RouteEpoch.Should().Be(1);
        var activated = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Single();
        activated.Route.FlipVersion.Should().Be(2);
        activated.Route.CaughtUpVersion.Should().Be(6);
        activated.Route.WarmStartedVersion.Should().Be(5);
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        var route = harness.Agent.State.StatusRoute!;
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        route.LegacyRouteReleased.Should().BeTrue();
        route.FlipVersion.Should().Be(2);
    }

    [Theory]
    [InlineData(AdoptedScopePath.Activation)]
    [InlineData(AdoptedScopePath.Ensure)]
    public async Task RestartWhileBlockedAfterRelease_FlipsOnly(AdoptedScopePath path)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.LegacyRouteReleased = true;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);

        await RunAdoptedScopePathAsync(harness, path);

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Removed.Should().BeEmpty();
        var route = harness.Agent.State.StatusRoute!;
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        route.FlipVersion.Should().Be(1);
        route.LegacyRouteReleased.Should().BeTrue();
    }

    [Fact]
    public async Task ActivationPath_WhenRouteActive_HealsLostTerminalRelayWithoutNewRouteDecision()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 4);
        state.StatusRoute.LegacyRouteReleased = true;
        // No fleet readers at all: an adopted route must not depend on a fresh grant to stay alive.
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);

        await harness.Agent.ActivateForTestAsync();

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.EventSourcing.Committed.Should().BeEmpty();
        var relay = harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId)).WhoseValue;
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                relay,
                SourceScopeActorId,
                TerminalActorId,
                ProjectionScopeStatusGAgent.AgentKind)
            .Should().BeTrue();
        harness.BindingAuthority!.Lookups.Should().Equal((SourceScopeActorId, LegacyActorId));
        harness.Runtime.CreatedByKind.Should().BeEmpty("the materializer already exists");
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(4);
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenRouteActiveAndTerminalActorMissing_RecreatesItWithoutLifecycleCommand()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        state.StatusRoute.LegacyRouteReleased = true;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            ActorCreate(ProjectionScopeStatusGAgent.AgentKind, TerminalActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.DispatchPort.Dispatched.Should().BeEmpty(
            "the materializer restarts itself on the first routed publication; the owner only heals relay + existence");
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(2);
    }

    [Fact]
    public async Task HandleEnsureAsync_AfterCompletedCutover_OnlyReassertsRelays()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission());
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        await harness.Agent.HandleStatusWriterCaughtUpAsync(
            BuildCaughtUpReport(routeEpoch: 1, observedVersion: harness.Agent.State.StatusRoute!.WarmStartedVersion));
        var committedAfterCutover = harness.EventSourcing.Committed.Count;
        var dispatchedAfterCutover = harness.DispatchPort.Dispatched.Count;
        harness.Journal.Clear();

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ], "a second ensure only re-asserts the terminal and observation relays; the flipped route is not re-negotiated");
        harness.EventSourcing.Committed.Should().HaveCount(committedAfterCutover);
        harness.DispatchPort.Dispatched.Should().HaveCount(dispatchedAfterCutover);
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        harness.Agent.State.StatusRoute.RouteEpoch.Should().Be(1);
    }

    // ── E. a BLOCKED route refuses observations until the flip ────────────────────────

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhileRouteBlocked_RefusesObservationThenProcessesAfterFlip()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Blocked);
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        var envelope = BuildForwardedRootEnvelope(BuildSourceStateAtVersion(3, flipVersion: 10), version: 3);

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(envelope);

        var blocked = (await act.Should().ThrowAsync<ProjectionScopeStatusRouteBlockedException>()).Which;
        blocked.ScopeActorId.Should().Be(SourceScopeActorId);
        blocked.RouteEpoch.Should().Be(1);
        harness.Journal.Should().BeEmpty("a refused observation records neither Received nor Attempted");
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.ReceivedEnvelopeTotal.Should().Be(0);
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(0);
        harness.Agent.State.HighestSeenVersion.Should().Be(0);

        // The flip (here: resumed on activation) unblocks the route; the same envelope is then
        // processed normally.
        await harness.Agent.ActivateForTestAsync();
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        harness.Journal.Clear();

        await harness.Agent.HandleObservedEnvelopeAsync(envelope);

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeEnvelopeReceivedEvent.Descriptor),
            Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor),
        ]);
        harness.Agent.State.ReceivedEnvelopeTotal.Should().Be(1);
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(1);
        harness.Agent.State.HighestSeenVersion.Should().Be(3);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    public async Task HandleObservedEnvelopeAsync_WhileRouteWarmingOrActive_ProcessesObservation(ProjectionScopeStatusRoutePhase phase)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, phase);
        state.StatusRoute.WarmStartedVersion = 9; // warming: not caught up, so nothing would flip
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);

        await harness.Agent.HandleObservedEnvelopeAsync(
            BuildForwardedRootEnvelope(BuildSourceStateAtVersion(2, flipVersion: 10), version: 2));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeEnvelopeReceivedEvent.Descriptor),
            Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor),
        ]);
        harness.Agent.State.HighestSeenVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleReplayAsync_WhileRouteBlocked_RefusesTheReplayedDispatchUntilTheFlip()
    {
        // The Blocked window is reached through the agent's own transition: the caught-up report
        // commits CaughtUp and Blocked, then the release of the legacy relay fails (relay store
        // down), leaving the route Blocked until the next activation completes the cutover. A
        // failure replay arriving inside that window is refused at dispatch (recorded as a failed
        // replay attempt, nothing attempted/published); after the flip the same replay dispatches.
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 1;
        var failedEnvelope = BuildForwardedRootEnvelope(BuildSourceStateAtVersion(2, flipVersion: 10), version: 2);
        state.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-1",
            Stage = "dispatch",
            EventId = "source-evt-2",
            EventType = ProjectionScopeWatermarkAdvancedEvent.Descriptor.FullName,
            SourceVersion = 2,
            SourceActorId = RootActorId,
            Reason = "boom",
            Envelope = failedEnvelope,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusRouteWarmingProbedEvent.Descriptor),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Publish(ReplayProjectionFailuresCommand.Descriptor, TopologyAudience.Self),
        ]);
        harness.Journal.Clear();
        harness.Streams.RemoveRelayFailure = new IOException("relay store unavailable");

        var blockedCutover = () => harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 1, observedVersion: 1));

        await blockedCutover.Should().ThrowAsync<IOException>();
        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStatusRouteCaughtUpEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteBlockedEvent.Descriptor),
            $"relay-remove-failed:{SourceScopeActorId}->{LegacyActorId}",
        ]);
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        harness.Streams.RemoveRelayFailure = null;
        harness.Journal.Clear();

        await harness.Agent.HandleReplayAsync(new ReplayProjectionFailuresCommand { MaxItems = 1 });

        // Refused, not re-attempted: the blocked route consumes no replay budget and records
        // no replay result; the failure stays exactly as it was until the flip.
        harness.Journal.Should().BeEmpty();
        harness.EventSourcing.Committed.Should().NotContain(evt => evt is ProjectionScopeFailureReplayedEvent);
        harness.EventSourcing.Committed.Should().NotContain(evt => evt is ProjectionScopeEnvelopeAttemptedEvent);
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(0);
        harness.Agent.State.Failures.Should().ContainSingle().Which.Attempts.Should().Be(0, "a refused replay is not a failed attempt");
        harness.Journal.Clear();

        // The next activation completes the cutover (the relay store is back); the replay then
        // dispatches and attempts the envelope.
        await harness.Agent.ActivateForTestAsync();

        harness.Journal.Should().Equal(
        [
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Dispatch(ReleaseProjectionScopeCommand.Descriptor, LegacyActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Publish(ReplayProjectionFailuresCommand.Descriptor, TopologyAudience.Self),
        ]);
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        harness.Journal.Clear();

        await harness.Agent.HandleReplayAsync(new ReplayProjectionFailuresCommand { MaxItems = 1 });

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor),
            Commit(ProjectionScopeFailureReplayedEvent.Descriptor),
        ], "after the flip the replay is dispatched (the test scope skips materialization, so the replay is recorded as not handled)");
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(1);
        harness.EventSourcing.Committed.OfType<ProjectionScopeFailureReplayedEvent>().Last().Reason.Should().NotContain("blocked");
    }

    [Fact]
    public async Task HandleResumeInFlightObservationAsync_WhileRouteBlocked_RefusesTheResumedDispatchUntilTheFlip()
    {
        var inFlightEnvelope = BuildForwardedRootEnvelope(BuildSourceStateAtVersion(3, flipVersion: 10), version: 3);
        var inFlightSource = new ProjectionSourceCoordinate
        {
            ActorId = RootActorId,
            StateVersion = 3,
            EventId = "source-evt-3",
        };
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Blocked);
        state.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = inFlightSource,
            Envelope = inFlightEnvelope,
            EventKind = ProjectionScopeWatermarkAdvancedEvent.Descriptor.FullName,
            StagedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        var harness = SourceScopeHarness.Build(
            registerFleetReaders: false,
            enablesDurableObservationRecovery: true,
            initialState: state);
        var resume = new ResumeProjectionInFlightObservationCommand { ExpectedSource = inFlightSource.Clone() };

        var act = () => harness.Agent.HandleResumeInFlightObservationAsync(resume);

        var blocked = (await act.Should().ThrowAsync<ProjectionScopeStatusRouteBlockedException>()).Which;
        blocked.ScopeActorId.Should().Be(SourceScopeActorId);
        blocked.RouteEpoch.Should().Be(1);
        harness.Journal.Should().BeEmpty("a refused resume records nothing and publishes nothing");
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(0);

        // The flip (resumed on activation) unblocks the route; the same resume then dispatches.
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Should().Equal(
        [
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Publish(ResumeProjectionInFlightObservationCommand.Descriptor, TopologyAudience.Self),
        ]);
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        harness.Journal.Clear();

        await harness.Agent.HandleResumeInFlightObservationAsync(resume);

        harness.Journal.Should().Equal([Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor)]);
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(1);
        harness.Agent.State.HighestSeenVersion.Should().Be(3);
    }

    // ── F. the legacy shadow's observation-side behaviour ──────────────────────────────

    [Fact]
    public async Task LegacyShadow_WhenSourceRouteIsWarmingTerminal_KeepsObservingWithoutReleasing()
    {
        var harness = BuildLegacyShadowHarness();
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Should().Equal([RelayUpsert(SourceScopeActorId, LegacyActorId)]);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)].TargetActorKind.Should().Be(LegacyStatusShadowKind);
        harness.Journal.Clear();
        var sourceState = BuildSourceStateAtVersion(3, flipVersion: 10);
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Warming);
        sourceState.StatusRoute.WarmStartedVersion = 3;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildCommittedSourceEnvelope(LegacyActorId, sourceState, 3));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeEnvelopeReceivedEvent.Descriptor),
            Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor),
        ], "while the terminal warms the legacy shadow is still the writer");
        harness.Publisher.SentTo.Should().BeEmpty();
        harness.Streams.Removed.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeFalse();
        harness.Agent.State.HighestSeenVersion.Should().Be(3);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    public async Task LegacyShadow_WhenSourceRouteIsTerminalInWritingPhase_ReleasesItselfAndSkipsObservation(
        ProjectionScopeStatusRoutePhase phase)
    {
        // A shadow that was (re)ensured after the block/flip sees the committed terminal route
        // in the very next source publication and releases itself on its own turn.
        var harness = BuildLegacyShadowHarness();
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        var sourceState = BuildSourceStateAtVersion(3, flipVersion: 10);
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, phase);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildCommittedSourceEnvelope(LegacyActorId, sourceState, 3));

        // The Released event detaches the observation as part of its transition, so no separate
        // attachment event follows it.
        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeReleasedEvent.Descriptor),
            RelayRemove(SourceScopeActorId, LegacyActorId),
        ]);
        harness.EventSourcing.Committed.Should().NotContain(evt => evt is ProjectionScopeEnvelopeReceivedEvent);
        harness.EventSourcing.Committed.Should().NotContain(evt => evt is ProjectionScopeEnvelopeAttemptedEvent);
        harness.Streams.Relays.Should().BeEmpty("afterwards the source stream carries no legacy relay");
        harness.Publisher.SentTo.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeTrue();
        harness.Agent.State.ObservationAttached.Should().BeFalse();
        harness.Agent.State.ReceivedEnvelopeTotal.Should().Be(0);
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(0);
        harness.Agent.State.HighestSeenVersion.Should().Be(0);
        harness.Journal.Clear();

        // A released shadow ignores whatever still drains onto it.
        await harness.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(LegacyActorId, BuildSourceStateAtVersion(4, flipVersion: 3), 4));

        harness.Journal.Should().BeEmpty();
        harness.EventSourcing.Committed.OfType<ProjectionScopeReleasedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task LegacyShadow_WhenSourceRouteIsLegacyWarming_ReportsCaughtUpToTheSourceAndKeepsObserving()
    {
        // Rollback in flight: the source committed a legacy route WARMING; this shadow is the
        // warming writer and proves delivery by reporting the observed version to its source.
        var harness = BuildLegacyShadowHarness();
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        var sourceState = BuildSourceStateAtVersion(7, flipVersion: 100);
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 2, ProjectionScopeStatusRoutePhase.Warming);
        sourceState.StatusRoute.WarmStartedVersion = 6;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildCommittedSourceEnvelope(LegacyActorId, sourceState, 7));

        harness.Journal.Should().Equal(
        [
            SendTo(ProjectionScopeStatusWriterCaughtUpEvent.Descriptor, SourceScopeActorId),
            Commit(ProjectionScopeEnvelopeReceivedEvent.Descriptor),
            Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor),
        ]);
        var sent = harness.Publisher.SentTo.Should().ContainSingle().Subject;
        sent.TargetActorId.Should().Be(SourceScopeActorId);
        sent.Event.Should().BeOfType<ProjectionScopeStatusWriterCaughtUpEvent>().Which.Should().Be(
            new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                RouteEpoch = 2,
                ObservedVersion = 7,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
        harness.Agent.State.Released.Should().BeFalse();
        harness.Streams.Removed.Should().BeEmpty();
        harness.Agent.State.HighestSeenVersion.Should().Be(7);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    public async Task LegacyShadow_WhenSourceRouteIsLegacyInWritingPhase_KeepsObservingWithoutReporting(
        ProjectionScopeStatusRoutePhase phase)
    {
        var harness = BuildLegacyShadowHarness();
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        var sourceState = BuildSourceStateAtVersion(8, flipVersion: 100);
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 2, phase);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildCommittedSourceEnvelope(LegacyActorId, sourceState, 8));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeEnvelopeReceivedEvent.Descriptor),
            Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor),
        ]);
        harness.Publisher.SentTo.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Fact]
    public async Task LegacyShadow_WhenSourceStateHasNoRoute_KeepsObserving()
    {
        var harness = BuildLegacyShadowHarness();
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();

        await harness.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(LegacyActorId, BuildSourceStateAtVersion(2, flipVersion: 3), 2));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeEnvelopeReceivedEvent.Descriptor),
            Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor),
        ]);
        harness.Publisher.SentTo.Should().BeEmpty();
        harness.Streams.Relays.Keys.Should().Equal((SourceScopeActorId, LegacyActorId));
        harness.Agent.State.Released.Should().BeFalse();
        harness.Agent.State.HighestSeenVersion.Should().Be(2);
    }

    public enum ObservedSourceRoute
    {
        TerminalBlocked,
        TerminalActive,
        LegacyWarming,
    }

    [Theory]
    [InlineData(ObservedSourceRoute.TerminalBlocked)]
    [InlineData(ObservedSourceRoute.TerminalActive)]
    [InlineData(ObservedSourceRoute.LegacyWarming)]
    public async Task NonStatusDurableScope_ObservingAScopeStateWithAnyStatusRoute_NeverReleasesOrReports(ObservedSourceRoute observed)
    {
        // Only the legacy status shadow interprets a source's status route; an ordinary durable
        // scope observing a scope state keeps going.
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: BuildActiveSourceState());
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        var observedState = BuildSourceStateAtVersion(3, flipVersion: 100);
        observedState.StatusRoute = observed switch
        {
            ObservedSourceRoute.TerminalBlocked => ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Blocked),
            ObservedSourceRoute.TerminalActive => ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Active),
            ObservedSourceRoute.LegacyWarming => ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, ProjectionScopeStatusRoutePhase.Warming),
            _ => throw new ArgumentOutOfRangeException(nameof(observed)),
        };

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedRootEnvelope(observedState, version: 3));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeEnvelopeReceivedEvent.Descriptor),
            Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor),
        ]);
        harness.Publisher.SentTo.Should().BeEmpty();
        harness.Streams.Removed.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeFalse();
    }

    // ── G. a legacy relay that reappears after the release ────────────────────────────

    [Theory]
    [InlineData(AdoptedScopePath.Activation)]
    [InlineData(AdoptedScopePath.Ensure)]
    public async Task ActiveReleasedRoute_WhenLegacyRelayReappears_RemovesItAndReleasesShadowAgainWithoutNewEvent(
        AdoptedScopePath path)
    {
        // The release is already recorded (LegacyRouteReleased == true); an older node
        // re-ensured the shadow, or a delayed legacy relay upsert landed after the cleanup. The
        // authoritative relay evidence is reconciled on every activation/ensure: the relay goes,
        // one Release is dispatched, and no route/release event is persisted again.
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 5);
        state.StatusRoute.LegacyRouteReleased = true;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();

        await RunAdoptedScopePathAsync(harness, path);

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            RelayRemove(SourceScopeActorId, LegacyActorId),
            Dispatch(ReleaseProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.BindingAuthority!.Lookups.Should().Equal((SourceScopeActorId, LegacyActorId));
        harness.EventSourcing.Committed.Should().BeEmpty("the release is already durably recorded");
        var release = harness.DispatchPort.Dispatched.Should().ContainSingle().Subject;
        release.actorId.Should().Be(LegacyActorId);
        release.command.Payload!.Unpack<ReleaseProjectionScopeCommand>().Should().Be(
            new ReleaseProjectionScopeCommand
            {
                RootActorId = SourceScopeActorId,
                ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
        release.command.Route.Direct.TargetActorId.Should().Be(LegacyActorId);
        harness.Streams.Relays.Keys.Should().BeEquivalentTo(
        [
            (SourceScopeActorId, TerminalActorId),
            (RootActorId, SourceScopeActorId),
        ], "afterwards the streams contain no legacy relay");
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(5);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeTrue();
    }

    [Fact]
    public async Task ActiveReleasedRoute_WhenLegacyRelayReappearsButShadowActorIsGone_OnlyRemovesRelay()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 5);
        state.StatusRoute.LegacyRouteReleased = true;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();

        await harness.Agent.ActivateForTestAsync();

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            RelayRemove(SourceScopeActorId, LegacyActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.Streams.Relays.Should().NotContainKey((SourceScopeActorId, LegacyActorId));
        harness.DispatchPort.Dispatched.Should().BeEmpty("no Release is sent to a legacy actor that does not exist");
        harness.EventSourcing.Committed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AdoptedScopePath.Activation)]
    [InlineData(AdoptedScopePath.Ensure)]
    public async Task ActiveReleasedRoute_WhenAuthorityReportsNoLegacyRelay_DoesNothing(AdoptedScopePath path)
    {
        // The reconciliation is driven by the authoritative relay evidence, not by the mere
        // existence of the legacy actor.
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 5);
        state.StatusRoute.LegacyRouteReleased = true;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);

        await RunAdoptedScopePathAsync(harness, path);

        harness.BindingAuthority!.Lookups.Should().Equal((SourceScopeActorId, LegacyActorId));
        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.Streams.Removed.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.EventSourcing.Committed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AdoptedScopePath.Activation)]
    [InlineData(AdoptedScopePath.Ensure)]
    public async Task ActiveReleasedRoute_WithoutBindingAuthority_SkipsReconciliationWithoutFailing(AdoptedScopePath path)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 5);
        state.StatusRoute.LegacyRouteReleased = true;
        var harness = SourceScopeHarness.Build(
            registerFleetReaders: false,
            registerBindingAuthority: false,
            initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = BuildLegacyStatusRelayBinding();

        var act = () => RunAdoptedScopePathAsync(harness, path);

        await act.Should().NotThrowAsync();
        harness.BindingAuthority.Should().BeNull();
        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.Streams.Removed.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, LegacyActorId),
            "without an authority the scope cannot prove the relay reappeared and leaves it to the shadow's own self-release");
    }

    // ── H. rollback to the legacy writer on an explicit revocation ────────────────────

    [Fact]
    public async Task ActiveTerminalRoute_WhenAdmissionRevoked_RollsBackThroughTheSamePhases()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1);
        state.StatusRoute.LegacyRouteReleased = true;
        state.StatusRoute.ActivationProof = BuildExpectedActivationProof();
        var harness = SourceScopeHarness.Build(
            admission: CreateAdmission(status: RuntimeFleetCapabilityGateStatus.Revoked),
            registerCallbackScheduler: true,
            initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Streams.Relays[(SourceScopeActorId, TerminalActorId)] = BuildTerminalStatusRelayBinding();

        // 1. Activation: the terminal route is maintained, the revocation is read and rollback
        //    warming starts at epoch+1 — the legacy shadow is ensured as the warming writer, the
        //    terminal relay is NOT removed (it is still the writer until the route is blocked).
        await harness.Agent.ActivateForTestAsync();

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            ActorCreate(LegacyStatusShadowKind, LegacyActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            Commit(ProjectionScopeStatusRouteWarmingStartedEvent.Descriptor),
            RetryTimeout(TimeSpan.FromSeconds(30)),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        var warming = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteWarmingStartedEvent>().Should().ContainSingle().Subject;
        warming.Route.ContractId.Should().Be(ProjectionScopeStatusRoutePolicy.LegacyContractId);
        warming.Route.ContractVersion.Should().Be(ProjectionScopeStatusRoutePolicy.LegacyContractVersion);
        warming.Route.RouteEpoch.Should().Be(2);
        warming.Route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        warming.Route.WarmStartedVersion.Should().Be(1);
        warming.Route.ActivatedAtUtc.Should().Be(Timestamp.FromDateTimeOffset(Now));
        warming.Route.ActivationProof.Should().BeNull("a rollback has no fleet grant as proof");
        harness.Streams.Relays.Keys.Should().BeEquivalentTo(
        [
            (SourceScopeActorId, TerminalActorId),
            (RootActorId, SourceScopeActorId),
        ]);
        harness.Streams.Removed.Should().BeEmpty();
        harness.Callbacks!.Timeouts.Should().ContainSingle("the rollback warming schedules its own continuation")
            .Which.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt.Should().Be(1);
        var route = harness.Agent.State.StatusRoute!;
        ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route).Should().BeTrue();
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        route.RouteEpoch.Should().Be(2);
        route.LegacyRouteReleased.Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(route).Should().BeFalse("the warming legacy shadow does not write yet");
        harness.Journal.Clear();

        // 2. The legacy shadow reports caught-up at epoch 2: block, release the terminal
        //    (relay removed, Release dispatched), record it, flip to the legacy route.
        await harness.Agent.HandleStatusWriterCaughtUpAsync(BuildCaughtUpReport(routeEpoch: 2, observedVersion: 1));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStatusRouteCaughtUpEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteBlockedEvent.Descriptor),
            RelayRemove(SourceScopeActorId, TerminalActorId),
            Dispatch(ReleaseProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor),
            Commit(ProjectionScopeStatusRouteActivatedEvent.Descriptor),
        ]);
        var terminalRelease = harness.DispatchPort.Dispatched
            .Should().ContainSingle(x => x.command.Payload!.Is(ReleaseProjectionScopeCommand.Descriptor)).Subject;
        terminalRelease.actorId.Should().Be(TerminalActorId);
        terminalRelease.command.Payload!.Unpack<ReleaseProjectionScopeCommand>().Should().Be(
            new ReleaseProjectionScopeCommand
            {
                RootActorId = SourceScopeActorId,
                ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusLegacyRouteReleasedEvent>().Single().RouteEpoch.Should().Be(2);
        var activated = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Single();
        activated.Route.ContractId.Should().Be(ProjectionScopeStatusRoutePolicy.LegacyContractId);
        activated.Route.RouteEpoch.Should().Be(2);
        activated.Route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        activated.Route.FlipVersion.Should().Be(5, "WarmingStarted 1, CaughtUp 2, Blocked 3, Released 4, Activated 5");
        activated.Route.LegacyRouteReleased.Should().BeTrue();
        harness.Streams.Relays.Keys.Should().Equal((RootActorId, SourceScopeActorId));
        harness.Callbacks.Timeouts.Should().ContainSingle("the flip schedules nothing more");
        route = harness.Agent.State.StatusRoute!;
        ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route).Should().BeTrue();
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        route.RouteEpoch.Should().Be(2);
        route.LegacyRouteReleased.Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(route).Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route, ProjectionScopeStatusGAgent.ContractId, ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse();
        harness.Journal.Clear();

        // 3. Afterwards the legacy writer is maintained: re-ensured on every activation, and
        //    while the gate stays revoked the adoption retry is scheduled again.
        await harness.Agent.ActivateForTestAsync();

        harness.Journal.Should().Equal(
        [
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RetryTimeout(TestScopeAgent.RetryDelays[0]),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.EventSourcing.CurrentVersion.Should().Be(5);
        harness.Callbacks.Timeouts.Should().HaveCount(2);
        harness.Journal.Clear();

        // 4. A fresh grant again: warming the terminal starts at epoch 3 (the terminal actor
        //    still exists, so it is only re-ensured) and schedules its own continuation.
        harness.Fleet!.Admission = CreateAdmission();
        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(
            harness.Callbacks.Timeouts[^1].TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>());

        harness.Journal.Should().Equal(
        [
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, TerminalActorId),
            Commit(ProjectionScopeStatusRouteWarmingStartedEvent.Descriptor),
            RetryTimeout(TimeSpan.FromSeconds(30)),
        ]);
        route = harness.Agent.State.StatusRoute!;
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route).Should().BeTrue();
        route.RouteEpoch.Should().Be(3);
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        route.WarmStartedVersion.Should().Be(6);
        route.LegacyRouteReleased.Should().BeFalse();
        route.ActivationProof.Should().Be(BuildExpectedActivationProof());
        harness.Callbacks.Timeouts.Should().HaveCount(3, "the adoption retry chain stops; the new warming schedules exactly one continuation");
        harness.Callbacks.Timeouts[^1].TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt.Should().Be(1);
    }

    public enum NonRevokingAdmission
    {
        Absent,
        Expired,
        RevokedForOtherContract,
        FreshGrant,
    }

    [Theory]
    [InlineData(NonRevokingAdmission.Absent)]
    [InlineData(NonRevokingAdmission.Expired)]
    [InlineData(NonRevokingAdmission.RevokedForOtherContract)]
    [InlineData(NonRevokingAdmission.FreshGrant)]
    public async Task ActiveTerminalRoute_WhenAdmissionIsNotAnExplicitRevocation_DoesNotRollBack(NonRevokingAdmission admission)
    {
        // Only an explicit revocation rolls an active terminal route back; absence or expiry of
        // the admission is not evidence that the terminal writer must stop.
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1);
        state.StatusRoute.LegacyRouteReleased = true;
        var harness = SourceScopeHarness.Build(
            admission: admission switch
            {
                NonRevokingAdmission.Absent => null,
                NonRevokingAdmission.Expired => CreateAdmission(validUntil: Now.AddSeconds(-1)),
                NonRevokingAdmission.RevokedForOtherContract => CreateAdmission(
                    status: RuntimeFleetCapabilityGateStatus.Revoked,
                    contractId: "some-other-contract"),
                NonRevokingAdmission.FreshGrant => CreateAdmission(),
                _ => throw new ArgumentOutOfRangeException(nameof(admission)),
            },
            registerCallbackScheduler: true,
            initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);

        await harness.Agent.ActivateForTestAsync();

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Callbacks!.Timeouts.Should().BeEmpty();
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(1);
        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(harness.Agent.State.StatusRoute).Should().BeTrue();
    }

    // ── scopes that never own a status route ──────────────────────────────────────────

    [Fact]
    public async Task HandleEnsureAsync_SessionObservationScope_NeverOwnsAStatusRoute()
    {
        var harness = SourceScopeHarness.Build(
            admission: CreateAdmission(),
            runtimeMode: ProjectionRuntimeMode.SessionObservation,
            scopeActorId: ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
                RootActorId,
                ProjectionKind,
                ProjectionRuntimeMode.SessionObservation,
                "session-1")));

        await harness.Agent.HandleEnsureAsync(new EnsureProjectionScopeCommand
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            SessionId = "session-1",
            Mode = ProjectionScopeMode.SessionObservation,
        });

        harness.EventSourcing.Committed.Should().NotContain(evt => evt is ProjectionScopeStatusRouteWarmingStartedEvent);
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Relays.Keys.Should().OnlyContain(key => key.Source == RootActorId);
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusMaterializationContext.ProjectionKindValue)]
    [InlineData(ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue)]
    public async Task HandleEnsureAsync_StatusWriterScopes_NeverOwnAStatusRouteNorEnsureAShadowOfTheirOwn(string statusKind)
    {
        var statusScopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            SourceScopeActorId,
            statusKind,
            ProjectionRuntimeMode.DurableMaterialization));
        var harness = SourceScopeHarness.Build(
            admission: CreateAdmission(),
            registerCallbackScheduler: true,
            scopeActorId: statusScopeActorId);

        await harness.Agent.HandleEnsureAsync(new EnsureProjectionScopeCommand
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = statusKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
        });

        harness.Agent.State.Active.Should().BeTrue();
        harness.Agent.State.ProjectionKind.Should().Be(statusKind);
        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            RelayUpsert(SourceScopeActorId, statusScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ], "a status writer has no status writer of its own (no mirror of the mirror)");
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Callbacks!.Timeouts.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    // ── I. epoch fence through the agent's real transition ────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Transition_WarmingStartedWithLowerOrEqualEpoch_LeavesRouteUnchanged(long staleEpoch)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        current.StatusRoute.LegacyRouteReleased = true;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteWarmingStartedEvent
        {
            Route = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(staleEpoch, ProjectionScopeStatusRoutePhase.Warming),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current, "the epoch fence rejects a stale or replayed warming outright");
        next.StatusRoute!.RouteEpoch.Should().Be(2);
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(next.StatusRoute).Should().BeTrue();
    }

    [Fact]
    public void Transition_WarmingStartedWithHigherEpoch_ReplacesRouteAsWarmingAndResetsCutoverFlags()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        current.StatusRoute.LegacyRouteReleased = true;
        current.StatusRoute.CaughtUpVersion = 5;
        current.StatusRoute.FlipVersion = 9;
        // The event may not smuggle a writing phase or pre-set cutover flags through.
        var warming = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 3, ProjectionScopeStatusRoutePhase.Active);
        warming.LegacyRouteReleased = true;
        warming.CaughtUpVersion = 7;
        warming.FlipVersion = 8;
        warming.WarmStartedVersion = 10;
        var occurredAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(1));

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteWarmingStartedEvent
        {
            Route = warming,
            OccurredAtUtc = occurredAt,
        });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.RouteEpoch.Should().Be(2, "appliers never mutate the current state in place");
        next.StatusRoute!.RouteEpoch.Should().Be(3);
        ProjectionScopeStatusRoutePolicy.IsLegacyRoute(next.StatusRoute).Should().BeTrue();
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        next.StatusRoute.LegacyRouteReleased.Should().BeFalse();
        next.StatusRoute.CaughtUpVersion.Should().Be(0);
        next.StatusRoute.FlipVersion.Should().Be(0);
        next.StatusRoute.WarmStartedVersion.Should().Be(10);
        next.UpdatedAtUtc.Should().Be(occurredAt);
    }

    [Theory]
    [InlineData(2, ProjectionScopeStatusRoutePhase.Warming, 3)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Active, 3)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Blocked, 3)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Warming, 2)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Warming, 1)]
    public void Transition_CaughtUpForOtherEpochNonWarmingRouteOrNotHigherVersion_LeavesRouteUnchanged(
        long eventEpoch,
        ProjectionScopeStatusRoutePhase currentPhase,
        long observedVersion)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, currentPhase);
        current.StatusRoute.CaughtUpVersion = 2;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteCaughtUpEvent
        {
            RouteEpoch = eventEpoch,
            ObservedVersion = observedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute!.CaughtUpVersion.Should().Be(2);
    }

    [Fact]
    public void Transition_CaughtUpWithHigherObservedVersionOnWarmingRoute_RecordsIt()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Warming);
        current.StatusRoute.CaughtUpVersion = 2;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteCaughtUpEvent
        {
            RouteEpoch = 1,
            ObservedVersion = 3,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.CaughtUpVersion.Should().Be(2);
        next.StatusRoute!.CaughtUpVersion.Should().Be(3);
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
    }

    [Theory]
    [InlineData(2, ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Active)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Blocked)]
    public void Transition_BlockedForOtherEpochOrNonWarmingRoute_LeavesRouteUnchanged(
        long eventEpoch,
        ProjectionScopeStatusRoutePhase currentPhase)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, currentPhase);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteBlockedEvent
        {
            RouteEpoch = eventEpoch,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute!.Phase.Should().Be(currentPhase);
    }

    [Fact]
    public void Transition_BlockedOnWarmingRouteOfSameEpoch_MovesToBlocked()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1, ProjectionScopeStatusRoutePhase.Warming);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteBlockedEvent
        {
            RouteEpoch = 1,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().NotBeSameAs(current);
        next.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        next.StatusRoute.RouteEpoch.Should().Be(1);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    public void Transition_RouteActivatedWithSameEpoch_FlipsAWarmingOrBlockedRoute(ProjectionScopeStatusRoutePhase currentPhase)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2, currentPhase);
        current.StatusRoute.WarmStartedVersion = 4;
        current.StatusRoute.CaughtUpVersion = 4;
        var flipped = current.StatusRoute.Clone();
        flipped.Phase = ProjectionScopeStatusRoutePhase.Active;
        flipped.FlipVersion = 6;
        flipped.LegacyRouteReleased = true;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = flipped,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().NotBeSameAs(current);
        next.StatusRoute!.RouteEpoch.Should().Be(2);
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        next.StatusRoute.FlipVersion.Should().Be(6);
        next.StatusRoute.WarmStartedVersion.Should().Be(4);
        next.StatusRoute.CaughtUpVersion.Should().Be(4);
        next.StatusRoute.LegacyRouteReleased.Should().BeTrue("a flip of the current cutover keeps the recorded release");
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    public void Transition_RouteActivatedWithSameEpochOnAWritingRoute_LeavesRouteUnchanged(ProjectionScopeStatusRoutePhase currentPhase)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2, currentPhase);
        current.StatusRoute.LegacyRouteReleased = true;
        var replay = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        replay.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = replay,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current, "a replayed activation at the same epoch never moves a writing route");
        next.StatusRoute!.LegacyRouteReleased.Should().BeTrue();
        next.StatusRoute.ActivatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Transition_RouteActivatedWithLowerEpoch_LeavesRouteUnchanged()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2, ProjectionScopeStatusRoutePhase.Warming);
        var stale = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1);
        stale.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = stale,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current, "the epoch fence rejects a stale or replayed activation outright");
        next.StatusRoute!.RouteEpoch.Should().Be(2);
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    public void Transition_RouteActivatedWithHigherEpoch_AppliesDirectlyAsActiveAndResetsLegacyRelease(
        ProjectionScopeStatusRoutePhase eventPhase)
    {
        // A phase-less route written by an earlier binary, or a direct activation at a higher
        // epoch: applied as ACTIVE; the release flag belongs to the new epoch and starts false.
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        current.StatusRoute.LegacyRouteReleased = true;
        var newer = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 3, eventPhase);
        newer.LegacyRouteReleased = true; // the event may not smuggle a pre-released flag through
        var occurredAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(1));

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = newer,
            OccurredAtUtc = occurredAt,
        });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.RouteEpoch.Should().Be(2, "appliers never mutate the current state in place");
        next.StatusRoute!.RouteEpoch.Should().Be(3);
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        next.StatusRoute.LegacyRouteReleased.Should().BeFalse();
        next.UpdatedAtUtc.Should().Be(occurredAt);
    }

    [Fact]
    public void Transition_RouteActivatedWithoutRoute_LeavesStateUnchanged()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteActivatedEvent
        {
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute.Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Transition_LegacyRouteReleasedForOtherEpoch_LeavesRouteUnchanged(long otherEpoch)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = otherEpoch,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute!.LegacyRouteReleased.Should().BeFalse();
    }

    [Fact]
    public void Transition_LegacyRouteReleasedForSameEpoch_RecordsTheRelease()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2, ProjectionScopeStatusRoutePhase.Blocked);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = 2,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.LegacyRouteReleased.Should().BeFalse();
        next.StatusRoute!.LegacyRouteReleased.Should().BeTrue();
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked, "the release does not flip the route by itself");
    }

    [Fact]
    public void Transition_LegacyRouteReleasedWithoutRoute_LeavesStateUnchanged()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = 1,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute.Should().BeNull();
    }

    // ── rolling / mixed status writers on one source ───────────────────────────────────

    [Fact]
    public async Task MixedWriters_GatedLegacyAndTerminal_ProduceMonotonicRouteCarryingDocumentWithoutConflict()
    {
        const long flipVersion = 3;
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static d => d.Id);
        var dispatcher = new RecordingStoreDispatcher(store);
        var clock = new FixedProjectionClock(Now);
        var legacyProjector = new ProjectionScopeStatusProjector(dispatcher, clock);
        var legacyContext = new ProjectionScopeStatusMaterializationContext { RootActorId = SourceScopeActorId };
        var terminal = TerminalAgentHarness.Build(dispatcher, clock);

        for (long version = 1; version <= 5; version++)
        {
            var state = BuildSourceStateAtVersion(version, flipVersion);
            await legacyProjector.ProjectAsync(
                legacyContext,
                BuildCommittedSourceEnvelope(LegacyActorId, state, version));
            await terminal.Agent.HandleObservedEnvelopeAsync(
                BuildCommittedSourceEnvelope(TerminalActorId, state, version));
        }

        dispatcher.Writes.Select(static w => (w.Version, w.Disposition)).Should().Equal(
            (1, ProjectionWriteDisposition.Applied),
            (2, ProjectionWriteDisposition.Applied),
            (3, ProjectionWriteDisposition.Applied),
            (4, ProjectionWriteDisposition.Applied),
            (5, ProjectionWriteDisposition.Applied));
        // The document carries the source's committed route (its write fence): whichever writer
        // produced a version, its bytes equal the shared mapping of that version's state.
        typeof(ProjectionScopeStatusDocument).GetProperty(nameof(ProjectionScopeStatusDocument.StatusRoute)).Should().NotBeNull();
        foreach (var write in dispatcher.Writes)
        {
            write.Document.ToByteArray()
                .Should().Equal(BuildExpectedDocument(write.Version, flipVersion, clock).ToByteArray());
            ((IProjectionRouteFencedReadModel)write.Document).RouteEpoch
                .Should().Be(write.Version >= flipVersion ? 1 : 0);
        }

        terminal.Agent.State.Active.Should().BeTrue();
        terminal.Agent.State.SourceScopeActorId.Should().Be(SourceScopeActorId);
        terminal.Agent.State.PendingWrite.Should().BeNull();
        terminal.EventSourcing.Committed.OfType<ProjectionScopeStatusTerminalStartedEvent>()
            .Should().ContainSingle("the first routed publication at the flip starts the terminal");

        // Late / replayed deliveries in the rolling window.
        await legacyProjector.ProjectAsync(
            legacyContext,
            BuildCommittedSourceEnvelope(LegacyActorId, BuildSourceStateAtVersion(2, flipVersion), 2));
        await legacyProjector.ProjectAsync(
            legacyContext,
            BuildCommittedSourceEnvelope(LegacyActorId, BuildSourceStateAtVersion(4, flipVersion), 4));
        await terminal.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(TerminalActorId, BuildSourceStateAtVersion(5, flipVersion), 5));
        await terminal.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(TerminalActorId, BuildSourceStateAtVersion(2, flipVersion), 2));

        dispatcher.Writes.Skip(5).Select(static w => (w.Version, w.Disposition)).Should().Equal(
            (2, ProjectionWriteDisposition.Stale),
            (5, ProjectionWriteDisposition.Duplicate));
        dispatcher.Writes.Should().NotContain(w => w.Disposition == ProjectionWriteDisposition.Conflict);
        dispatcher.Writes.Should().NotContain(w => w.Disposition == ProjectionWriteDisposition.Gap);
        terminal.Agent.State.PendingWrite.Should().BeNull();

        var final = await store.GetAsync(SourceScopeActorId);
        final.Should().NotBeNull();
        final!.StateVersion.Should().Be(5);
        final.LastEventId.Should().Be("source-evt-5");
        final.LastSuccessfulVersion.Should().Be(50);
        final.StatusRoute.Should().NotBeNull();
        final.StatusRoute!.RouteEpoch.Should().Be(1);
        final.ToByteArray().Should().Equal(BuildExpectedDocument(5, flipVersion, clock).ToByteArray());
    }

    [Fact]
    public async Task MixedWriters_OldBinaryLegacyWriterOverlapping_SameEpochDuplicatesHigherEpochTakesOverLowerEpochStale()
    {
        // An old-binary legacy shadow that knows neither routes nor the gate writes every version
        // through the route-less mapping (document epoch 0); the new-binary legacy shadow writes
        // while the route selects it; the terminal writes from the flip (document epoch 1).
        // Same source version, same epoch: byte-identical duplicate. Same source version,
        // strictly higher epoch: Applied (the takeover). Lower epoch at the same version: Stale.
        // Never a conflict.
        const long flipVersion = 3;
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static d => d.Id);
        var dispatcher = new RecordingStoreDispatcher(store);
        var clock = new FixedProjectionClock(Now);
        var legacyProjector = new ProjectionScopeStatusProjector(dispatcher, clock);
        var legacyContext = new ProjectionScopeStatusMaterializationContext { RootActorId = SourceScopeActorId };
        var terminal = TerminalAgentHarness.Build(dispatcher, clock);

        for (long version = 1; version <= 5; version++)
        {
            var state = BuildSourceStateAtVersion(version, flipVersion);
            var terminalEnvelope = BuildCommittedSourceEnvelope(TerminalActorId, state, version);
            var legacyEnvelope = BuildCommittedSourceEnvelope(LegacyActorId, state, version);
            if (version % 2 == 1)
            {
                await WriteAsOldBinaryLegacyAsync(dispatcher, legacyEnvelope, clock);
                await legacyProjector.ProjectAsync(legacyContext, legacyEnvelope);
                await terminal.Agent.HandleObservedEnvelopeAsync(terminalEnvelope);
            }
            else
            {
                await legacyProjector.ProjectAsync(legacyContext, legacyEnvelope);
                await terminal.Agent.HandleObservedEnvelopeAsync(terminalEnvelope);
                await WriteAsOldBinaryLegacyAsync(dispatcher, legacyEnvelope, clock);
            }
        }

        dispatcher.Writes.Select(static w => (w.Version, w.Disposition, ((IProjectionRouteFencedReadModel)w.Document).RouteEpoch))
            .Should().Equal(
                (1, ProjectionWriteDisposition.Applied, 0),   // old binary
                (1, ProjectionWriteDisposition.Duplicate, 0), // gated legacy: same epoch, same bytes
                (2, ProjectionWriteDisposition.Applied, 0),   // gated legacy
                (2, ProjectionWriteDisposition.Duplicate, 0), // old binary
                (3, ProjectionWriteDisposition.Applied, 0),   // old binary (gated legacy is superseded and silent)
                (3, ProjectionWriteDisposition.Applied, 1),   // terminal: same version, higher epoch => takeover
                (4, ProjectionWriteDisposition.Applied, 1),   // terminal
                (4, ProjectionWriteDisposition.Stale, 0),     // old binary: same version, lower epoch
                (5, ProjectionWriteDisposition.Applied, 0),   // old binary: higher source version always wins
                (5, ProjectionWriteDisposition.Applied, 1));  // terminal takeover again
        dispatcher.Writes.Should().NotContain(w => w.Disposition == ProjectionWriteDisposition.Conflict);
        dispatcher.Writes.Should().NotContain(w => w.Disposition == ProjectionWriteDisposition.Gap);
        terminal.Agent.State.PendingWrite.Should().BeNull("neither a duplicate nor a takeover is a rejected write");
        foreach (var write in dispatcher.Writes)
        {
            var expected = ((IProjectionRouteFencedReadModel)write.Document).RouteEpoch == 0
                ? BuildExpectedOldBinaryDocument(write.Version, flipVersion, clock)
                : BuildExpectedDocument(write.Version, flipVersion, clock);
            write.Document.ToByteArray().Should().Equal(expected.ToByteArray());
        }

        var final = await store.GetAsync(SourceScopeActorId);
        final!.StateVersion.Should().Be(5);
        final.LastSuccessfulVersion.Should().Be(50);
        final.StatusRoute!.RouteEpoch.Should().Be(1, "the terminal's takeover is the surviving document");
        final.ToByteArray().Should().Equal(BuildExpectedDocument(5, flipVersion, clock).ToByteArray());
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private static string Commit(MessageDescriptor descriptor) => $"commit:{descriptor.Name}";
    private static string RelayUpsert(string source, string target) => $"relay-upsert:{source}->{target}";
    private static string RelayRemove(string source, string target) => $"relay-remove:{source}->{target}";
    private static string ActorCreate(string agentKind, string actorId) => $"actor-create:{agentKind}:{actorId}";
    private static string Dispatch(MessageDescriptor command, string actorId) => $"dispatch:{command.Name}:{actorId}";
    private static string RetryTimeout(TimeSpan dueTime) => $"timeout:{TestScopeAgent.RetryCallbackId}:{dueTime}";
    private static string SendTo(MessageDescriptor evt, string targetActorId) => $"send-to:{evt.Name}:{targetActorId}";
    private static string Publish(MessageDescriptor evt, TopologyAudience audience) => $"publish:{evt.Name}:{audience}";

    private static SourceScopeHarness BuildNoGrantHarness(NoGrantReason reason, bool registerCallbackScheduler) =>
        reason switch
        {
            NoGrantReason.AdmissionMissing => SourceScopeHarness.Build(
                admission: null,
                registerCallbackScheduler: registerCallbackScheduler),
            NoGrantReason.MembershipMismatch => SourceScopeHarness.Build(
                admission: CreateAdmission(),
                membership: new RuntimeLocalMembershipIdentity(8, "digest-a", "revision-a", "member-a", "inc-a"),
                registerCallbackScheduler: registerCallbackScheduler),
            NoGrantReason.GateRevoked => SourceScopeHarness.Build(
                admission: CreateAdmission(status: RuntimeFleetCapabilityGateStatus.Revoked),
                registerCallbackScheduler: registerCallbackScheduler),
            NoGrantReason.WrongCapability => SourceScopeHarness.Build(
                admission: CreateAdmission(capability: RuntimeFleetCapability.ProjectionIncrementalGraphV1),
                registerCallbackScheduler: registerCallbackScheduler),
            NoGrantReason.AdmissionExpired => SourceScopeHarness.Build(
                admission: CreateAdmission(validUntil: Now.AddSeconds(-1)),
                registerCallbackScheduler: registerCallbackScheduler),
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

    /// <summary>The legacy status shadow of <see cref="SourceScopeActorId"/> as a scope actor under test.</summary>
    private static SourceScopeHarness BuildLegacyShadowHarness() =>
        SourceScopeHarness.Build(
            registerFleetReaders: false,
            scopeActorId: LegacyActorId,
            agentKind: LegacyStatusShadowKind,
            initialState: BuildActiveLegacyShadowState());

    private static Task RunAdoptedScopePathAsync(SourceScopeHarness harness, AdoptedScopePath path) =>
        path switch
        {
            AdoptedScopePath.Activation => harness.Agent.ActivateForTestAsync(),
            AdoptedScopePath.Ensure => harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand()),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };

    private static ProjectionScopeStatusWriterCaughtUpEvent BuildCaughtUpReport(
        long routeEpoch,
        long observedVersion,
        string? sourceScopeActorId = null) =>
        new()
        {
            SourceScopeActorId = sourceScopeActorId ?? SourceScopeActorId,
            RouteEpoch = routeEpoch,
            ObservedVersion = observedVersion,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };

    private static EnsureProjectionScopeCommand BuildDurableEnsureCommand() =>
        new()
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
        };

    private static ProjectionScopeState BuildActiveSourceState() =>
        new()
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            Released = false,
            ObservationAttached = true,
            ActivationGeneration = 1,
        };

    /// <summary>The legacy status shadow of <see cref="SourceScopeActorId"/>: active, attached, durable.</summary>
    private static ProjectionScopeState BuildActiveLegacyShadowState() =>
        new()
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            Released = false,
            ObservationAttached = true,
            ActivationGeneration = 1,
        };

    private static StreamForwardingBinding BuildLegacyStatusRelayBinding() =>
        ProjectionScopeObservationRelayBinding.Create(
            SourceScopeActorId,
            LegacyActorId,
            LegacyStatusShadowKind,
            activationGeneration: 1);

    private static StreamForwardingBinding BuildTerminalStatusRelayBinding() =>
        ProjectionScopeObservationRelayBinding.Create(
            SourceScopeActorId,
            TerminalActorId,
            ProjectionScopeStatusGAgent.AgentKind,
            activationGeneration: 1);

    private static ProjectionMaterializationActivationProof BuildExpectedActivationProof()
    {
        var admission = CreateAdmission();
        return new ProjectionMaterializationActivationProof
        {
            AuthorityStateVersion = admission.AuthorityStateVersion,
            CapabilityEpoch = admission.CapabilityEpoch,
            MembershipEpoch = admission.MembershipEpoch,
            MembershipDigest = admission.MembershipDigest,
            DeploymentRevision = admission.DeploymentRevision,
            ValidatedAtUtc = Timestamp.FromDateTimeOffset(Now),
            ValidUntilUtc = admission.MembershipValidUntil,
        };
    }

    /// <summary>
    /// A committed source state at <paramref name="version"/>: no route before
    /// <paramref name="flipVersion"/>, the ACTIVE terminal route (epoch 1, released) from it on.
    /// </summary>
    private static ProjectionScopeState BuildSourceStateAtVersion(long version, long flipVersion)
    {
        var state = BuildActiveSourceState();
        state.LastSuccessfulVersion = version * 10;
        state.HighestSeenVersion = version * 10;
        state.SuccessfulMaterializationTotal = version;
        if (version >= flipVersion)
        {
            state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1);
            state.StatusRoute.LegacyRouteReleased = true;
            state.StatusRoute.FlipVersion = flipVersion;
        }

        return state;
    }

    private static EventEnvelope BuildCommittedSourceEnvelope(
        string targetActorId,
        ProjectionScopeState state,
        long version) =>
        StreamForwardingRules.BuildForwardedEnvelope(
            BuildObserverPublication(SourceScopeActorId, state, version),
            sourceStreamId: SourceScopeActorId,
            targetStreamId: targetActorId,
            StreamForwardingMode.HandleThenForward);

    /// <summary>A root publication forwarded to the source scope under test (its own observation input).</summary>
    private static EventEnvelope BuildForwardedRootEnvelope(ProjectionScopeState observedState, long version) =>
        StreamForwardingRules.BuildForwardedEnvelope(
            BuildObserverPublication(RootActorId, observedState, version),
            sourceStreamId: RootActorId,
            targetStreamId: SourceScopeActorId,
            StreamForwardingMode.HandleThenForward);

    private static EventEnvelope BuildObserverPublication(
        string publisherActorId,
        ProjectionScopeState state,
        long version)
    {
        var timestamp = Timestamp.FromDateTimeOffset(Now.AddSeconds(version));
        return new EventEnvelope
        {
            Id = $"outer-{publisherActorId}-{version}",
            Timestamp = timestamp,
            Route = EnvelopeRouteSemantics.CreateObserverPublication(publisherActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = publisherActorId,
                    EventId = $"source-evt-{version}",
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

    /// <summary>
    /// The status document a route-aware writer must produce for <paramref name="version"/>: the
    /// shared mapping of that version's committed source state, route included.
    /// </summary>
    private static ProjectionScopeStatusDocument BuildExpectedDocument(
        long version,
        long flipVersion,
        IProjectionClock clock) =>
        MapDocument(BuildSourceStateAtVersion(version, flipVersion), version, clock);

    /// <summary>
    /// What an old-binary legacy shadow that never knew about routes would have written for
    /// <paramref name="version"/>: the same mapping with the route stripped (document epoch 0).
    /// </summary>
    private static ProjectionScopeStatusDocument BuildExpectedOldBinaryDocument(
        long version,
        long flipVersion,
        IProjectionClock clock)
    {
        var routelessState = BuildSourceStateAtVersion(version, flipVersion);
        routelessState.StatusRoute = null;
        return MapDocument(routelessState, version, clock);
    }

    private static ProjectionScopeStatusDocument MapDocument(ProjectionScopeState state, long version, IProjectionClock clock)
    {
        var envelope = BuildCommittedSourceEnvelope(TerminalActorId, state, version);
        CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var unpackedState)
            .Should().BeTrue();
        return ProjectionScopeStatusDocumentMapper.Map(
            unpackedState!,
            stateEvent!,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, clock.UtcNow));
    }

    private static async Task WriteAsOldBinaryLegacyAsync(
        IProjectionWriteDispatcher<ProjectionScopeStatusDocument> dispatcher,
        EventEnvelope envelope,
        IProjectionClock clock)
    {
        CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var state)
            .Should().BeTrue();
        var routelessState = state!.Clone();
        routelessState.StatusRoute = null;
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, clock.UtcNow);
        await dispatcher.UpsertAsync(ProjectionScopeStatusDocumentMapper.Map(routelessState, stateEvent!, updatedAt));
    }

    private static RuntimeFleetCapabilityAdmission CreateAdmission(
        RuntimeFleetCapability capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
        RuntimeFleetCapabilityGateStatus status = RuntimeFleetCapabilityGateStatus.Open,
        string contractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
        DateTimeOffset? validUntil = null)
    {
        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = capability,
            Status = status,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 9,
            CapabilityEpoch = 3,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            MinimumReaderContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion,
            MembershipObservedAt = Timestamp.FromDateTimeOffset(Now.AddSeconds(-5)),
            MembershipValidUntil = Timestamp.FromDateTimeOffset(validUntil ?? Now.AddMinutes(1)),
            ActiveMemberCount = 1,
            ConfirmedMemberCount = 1,
            MembershipDigest = "digest-a",
            ContractId = contractId,
        };
        admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember
        {
            MemberId = "member-a",
            Incarnation = "inc-a",
        });
        return admission;
    }

    private static void SetAgentId(GAgentBase agent, string id) =>
        typeof(GAgentBase)
            .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(agent, id);

    // ── source scope harness ───────────────────────────────────────────────────────────

    private sealed class SourceScopeHarness
    {
        public required TestScopeAgent Agent { get; init; }
        public required TrackingEventSourcing<ProjectionScopeState> EventSourcing { get; init; }
        public required RecordingStreamProvider Streams { get; init; }
        public required RecordingActorRuntime Runtime { get; init; }
        public required RecordingActorDispatchPort DispatchPort { get; init; }
        public required RecordingEventPublisher Publisher { get; init; }
        public required List<string> Journal { get; init; }
        public required MutableFleetAdmissionSource? Fleet { get; init; }
        public required RecordingCallbackScheduler? Callbacks { get; init; }
        public required RecordingBindingAuthority? BindingAuthority { get; init; }

        public static SourceScopeHarness Build(
            RuntimeFleetCapabilityAdmission? admission = null,
            bool registerFleetReaders = true,
            bool registerRuntimeServices = true,
            RuntimeLocalMembershipIdentity? membership = null,
            ProjectionRuntimeMode runtimeMode = ProjectionRuntimeMode.DurableMaterialization,
            string? scopeActorId = null,
            string? agentKind = null,
            ProjectionScopeState? initialState = null,
            bool registerCallbackScheduler = false,
            bool registerBindingAuthority = true,
            bool registerLegacyStatusShadowKind = true,
            bool enablesDurableObservationRecovery = false)
        {
            var journal = new List<string>();
            var agent = new TestScopeAgent(runtimeMode, enablesDurableObservationRecovery);
            SetAgentId(agent, scopeActorId ?? SourceScopeActorId);
            var eventSourcing = new TrackingEventSourcing<ProjectionScopeState>(agent.Transition, journal);
            agent.EventSourcing = eventSourcing;
            if (initialState != null)
                agent.State.MergeFrom(initialState);

            var streams = new RecordingStreamProvider(journal);
            var runtime = new RecordingActorRuntime(journal);
            var dispatchPort = new RecordingActorDispatchPort(journal);
            var publisher = new RecordingEventPublisher(journal);
            agent.EventPublisher = publisher;

            // The kind registry mirrors the production registrations the source scope relies on:
            // its own kind (its relay binding) and the legacy status shadow kind (the actor it
            // ensures by kind), the latter exactly as ProjectionScopeStatusRuntimeRegistration
            // registers it.
            var kinds = new Dictionary<Type, string> { [typeof(TestScopeAgent)] = agentKind ?? TestScopeAgentKind };
            if (registerLegacyStatusShadowKind)
            {
                var legacyRegistration = ProjectionScopeAgentRegistration
                    .Create<ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>>();
                kinds[legacyRegistration.ImplementationType] = legacyRegistration.Kind;
            }

            var services = new ServiceCollection();
            services.AddSingleton<Func<ProjectionRuntimeScopeKey, TestContext>>(
                static _ => new TestContext(RootActorId, ProjectionKind));
            services.AddSingleton<IStreamProvider>(streams);
            services.AddSingleton<IAgentKindRegistry>(new TestAgentKindRegistry(kinds));
            if (registerRuntimeServices)
            {
                services.AddSingleton<IActorRuntime>(runtime);
                services.AddSingleton<IActorDispatchPort>(dispatchPort);
            }

            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            MutableFleetAdmissionSource? fleet = null;
            if (registerFleetReaders)
            {
                fleet = new MutableFleetAdmissionSource(admission, membership);
                services.AddSingleton<IRuntimeFleetCapabilityAdmissionReader>(fleet);
                services.AddSingleton<IRuntimeLocalMembershipIdentityReader>(fleet);
            }

            RecordingCallbackScheduler? callbacks = null;
            if (registerCallbackScheduler)
            {
                callbacks = new RecordingCallbackScheduler(journal);
                services.AddSingleton<IActorRuntimeCallbackScheduler>(callbacks);
            }

            // The authority answers from the same relay dictionary the scope writes to and
            // removes from, so its view is consistent with the scope's own relay operations.
            RecordingBindingAuthority? bindingAuthority = null;
            if (registerBindingAuthority)
            {
                bindingAuthority = new RecordingBindingAuthority(streams);
                services.AddSingleton<IStreamForwardingBindingAuthority>(bindingAuthority);
            }

            agent.Services = services.BuildServiceProvider();
            return new SourceScopeHarness
            {
                Agent = agent,
                EventSourcing = eventSourcing,
                Streams = streams,
                Runtime = runtime,
                DispatchPort = dispatchPort,
                Publisher = publisher,
                Journal = journal,
                Fleet = fleet,
                Callbacks = callbacks,
                BindingAuthority = bindingAuthority,
            };
        }
    }

    private sealed class TestScopeAgent : ProjectionScopeGAgentBase<TestContext>
    {
        public static string RetryCallbackId => StatusRouteAdoptionRetryCallbackId;
        public static TimeSpan[] RetryDelays => StatusRouteAdoptionRetryDelays;

        private readonly ProjectionRuntimeMode _runtimeMode;
        private readonly bool _enablesDurableObservationRecovery;

        public TestScopeAgent(ProjectionRuntimeMode runtimeMode, bool enablesDurableObservationRecovery = false)
        {
            _runtimeMode = runtimeMode;
            _enablesDurableObservationRecovery = enablesDurableObservationRecovery;
        }

        protected override ProjectionRuntimeMode RuntimeMode => _runtimeMode;

        protected override bool EnablesDurableObservationRecovery => _enablesDurableObservationRecovery;

        public Task ActivateForTestAsync() => OnActivateAsync(CancellationToken.None);

        /// <summary>Exposes the agent's real transition routing so appliers are exercised through it.</summary>
        public ProjectionScopeState Transition(ProjectionScopeState current, IMessage evt) =>
            TransitionState(current, evt);

        protected override ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
            TestContext context,
            EventEnvelope envelope,
            CancellationToken ct) =>
            ValueTask.FromResult(ProjectionScopeDispatchResult.Skip());
    }

    private sealed record TestContext(string RootActorId, string ProjectionKind)
        : IProjectionMaterializationContext;

    // ── terminal materializer harness (used only for the mixed-writer scenarios) ───────

    private sealed class TerminalAgentHarness
    {
        public required ProjectionScopeStatusGAgent Agent { get; init; }
        public required TrackingEventSourcing<ProjectionScopeStatusTerminalState> EventSourcing { get; init; }

        public static TerminalAgentHarness Build(
            IProjectionWriteDispatcher<ProjectionScopeStatusDocument> dispatcher,
            IProjectionClock clock)
        {
            var journal = new List<string>();
            var agent = new ProjectionScopeStatusGAgent();
            SetAgentId(agent, TerminalActorId);
            var eventSourcing = new TrackingEventSourcing<ProjectionScopeStatusTerminalState>(TransitionTerminal, journal);
            agent.EventSourcing = eventSourcing;
            agent.EventPublisher = new RecordingEventPublisher(journal);

            var services = new ServiceCollection();
            services.AddSingleton(dispatcher);
            services.AddSingleton(clock);
            services.AddSingleton<IStreamProvider>(new RecordingStreamProvider(journal));
            services.AddSingleton<IAgentKindRegistry>(new TestAgentKindRegistry(new Dictionary<Type, string>
            {
                [typeof(ProjectionScopeStatusGAgent)] = ProjectionScopeStatusGAgent.AgentKind,
            }));
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

    // ── fakes ──────────────────────────────────────────────────────────────────────────

    private sealed class TrackingEventSourcing<TState> : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly List<IMessage> _pending = [];
        private readonly Func<TState, IMessage, TState> _transition;
        private readonly List<string> _journal;

        public TrackingEventSourcing(Func<TState, IMessage, TState> transition, List<string> journal)
        {
            _transition = transition;
            _journal = journal;
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
                _journal.Add($"commit:{evt.Descriptor.Name}");
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

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly List<string> _journal;

        public RecordingStreamProvider(List<string> journal)
        {
            _journal = journal;
        }

        public Dictionary<(string Source, string Target), StreamForwardingBinding> Relays { get; } = [];
        public List<(string Source, string Target)> Removed { get; } = [];

        /// <summary>When set, every relay removal fails with this exception (relay store outage).</summary>
        public Exception? RemoveRelayFailure { get; set; }

        public IStream GetStream(string actorId) => new RecordingStream(this, actorId);

        private sealed class RecordingStream : IStream
        {
            private readonly RecordingStreamProvider _owner;

            public RecordingStream(RecordingStreamProvider owner, string streamId)
            {
                _owner = owner;
                StreamId = streamId;
            }

            public string StreamId { get; }

            public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage =>
                throw new NotSupportedException();

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : IMessage, new() =>
                throw new NotSupportedException();

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
            {
                binding.SourceStreamId.Should().Be(StreamId, "a stream only owns relays whose source is itself");
                _owner.Relays[(StreamId, binding.TargetStreamId)] = binding;
                _owner._journal.Add($"relay-upsert:{StreamId}->{binding.TargetStreamId}");
                return Task.CompletedTask;
            }

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
            {
                if (_owner.RemoveRelayFailure != null)
                {
                    _owner._journal.Add($"relay-remove-failed:{StreamId}->{targetStreamId}");
                    throw _owner.RemoveRelayFailure;
                }

                _owner.Relays.Remove((StreamId, targetStreamId));
                _owner.Removed.Add((StreamId, targetStreamId));
                _owner._journal.Add($"relay-remove:{StreamId}->{targetStreamId}");
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(
                    _owner.Relays.Where(kv => kv.Key.Source == StreamId).Select(kv => kv.Value).ToList());
        }
    }

    /// <summary>
    /// Authoritative exact-binding lookup backed by <see cref="RecordingStreamProvider.Relays"/>:
    /// what the scope upserts/removes on its streams is exactly what the authority reports.
    /// </summary>
    private sealed class RecordingBindingAuthority : IStreamForwardingBindingAuthority
    {
        private readonly RecordingStreamProvider _streams;

        public RecordingBindingAuthority(RecordingStreamProvider streams)
        {
            _streams = streams;
        }

        public List<(string Source, string Target)> Lookups { get; } = [];

        public Task<StreamForwardingBinding?> GetAsync(
            string sourceStreamId,
            string targetStreamId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Lookups.Add((sourceStreamId, targetStreamId));
            return Task.FromResult(
                _streams.Relays.TryGetValue((sourceStreamId, targetStreamId), out var binding) ? binding : null);
        }
    }

    private sealed class TestAgentKindRegistry : IAgentKindRegistry
    {
        private readonly IReadOnlyDictionary<Type, string> _kinds;

        public TestAgentKindRegistry(IReadOnlyDictionary<Type, string> kinds)
        {
            _kinds = kinds;
        }

        public AgentImplementation Resolve(string kind) => throw new UnknownAgentKindException(kind);

        public bool TryResolve(string kind, out AgentImplementation implementation)
        {
            implementation = null!;
            return false;
        }

        public bool TryGetKindForAgentType(Type agentType, out string kind)
        {
            if (_kinds.TryGetValue(agentType, out var registered))
            {
                kind = registered;
                return true;
            }

            kind = string.Empty;
            return false;
        }

        public bool TryGetKind(AgentImplementation implementation, out string kind)
        {
            kind = string.Empty;
            return false;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly List<string> _journal;

        public RecordingActorRuntime(List<string> journal)
        {
            _journal = journal;
        }

        public HashSet<string> ExistingActorIds { get; } = [];
        public List<(string agentKind, string actorId)> CreatedByKind { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException("the source scope creates its status writers by kind only");

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException("the source scope creates its status writers by kind only");

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            ExistingActorIds.Add(actorId);
            CreatedByKind.Add((agentKind, actorId));
            _journal.Add($"actor-create:{agentKind}:{actorId}");
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActorIds.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        private readonly List<string> _journal;

        public RecordingActorDispatchPort(List<string> journal)
        {
            _journal = journal;
        }

        public List<(string actorId, EventEnvelope command)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatched.Add((actorId, envelope));
            var payloadName = envelope.Payload?.TypeUrl.Split('/')[^1].Split('.')[^1] ?? string.Empty;
            _journal.Add($"dispatch:{payloadName}:{actorId}");
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    /// <summary>Records the actor's outbound messages (the caught-up report of a warming writer).</summary>
    private sealed class RecordingEventPublisher : IEventPublisher
    {
        private readonly List<string> _journal;

        public RecordingEventPublisher(List<string> journal)
        {
            _journal = journal;
        }

        public List<(string TargetActorId, IMessage Event)> SentTo { get; } = [];
        public List<(TopologyAudience Audience, IMessage Event)> Published { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add((audience, evt));
            _journal.Add($"publish:{evt.Descriptor.Name}:{audience}");
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            SentTo.Add((targetActorId, evt));
            _journal.Add($"send-to:{evt.Descriptor.Name}:{targetActorId}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly List<string> _journal;

        public RecordingCallbackScheduler(List<string> journal)
        {
            _journal = journal;
        }

        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            _journal.Add($"timeout:{request.CallbackId}:{request.DueTime}");
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableFleetAdmissionSource
        : IRuntimeFleetCapabilityAdmissionReader, IRuntimeLocalMembershipIdentityReader
    {
        public MutableFleetAdmissionSource(
            RuntimeFleetCapabilityAdmission? admission,
            RuntimeLocalMembershipIdentity? membership)
        {
            Admission = admission;
            Membership = membership ?? new RuntimeLocalMembershipIdentity(7, "digest-a", "revision-a", "member-a", "inc-a");
        }

        public RuntimeFleetCapabilityAdmission? Admission { get; set; }
        public RuntimeLocalMembershipIdentity? Membership { get; set; }

        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV1 ? Admission?.Clone() : null);
        }

        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Membership);
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

    private sealed class RecordingStoreDispatcher : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
    {
        private readonly InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string> _store;

        public RecordingStoreDispatcher(InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string> store)
        {
            _store = store;
        }

        public List<(long Version, ProjectionWriteDisposition Disposition, ProjectionScopeStatusDocument Document)> Writes { get; } = [];

        public async Task<ProjectionWriteResult> UpsertAsync(
            ProjectionScopeStatusDocument readModel,
            CancellationToken ct = default)
        {
            var result = await _store.UpsertAsync(readModel, ct);
            Writes.Add((readModel.StateVersion, result.Disposition, readModel.Clone()));
            return result;
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
