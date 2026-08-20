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
using Aevatar.Foundation.Core.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Type = System.Type;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// Source-scope ownership of the status write route (#3476). Phase A is a forward-only bridge:
/// new binaries never start a V2 cutover. They freeze committed WARMING/BLOCKED routes until the
/// distinct bridge contract is durably quiesced, then repair only those persisted cutovers with
/// fresh authenticated writer and drain proofs. Phase B still requires a separate OPEN gate.
/// </summary>
public sealed class ProjectionScopeStatusRouteActivationTests
{
    private const string RootActorId = "root-actor";
    private const string ProjectionKind = "test-kind";
    private const string TestScopeAgentKind = "projection.materialization-scope.test-context";

    /// <summary>
    /// The previous terminal contract as an older binary wrote it into a route. This binary
    /// neither advertises nor re-admits it, so it lives only in routes and fixtures.
    /// </summary>
    private const string PreviousTerminalContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1;

    private const long PreviousTerminalContractVersion =
        1;

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

    public enum FrozenRouteKind
    {
        Terminal,
        Legacy,
    }

    public enum FrozenRoutePath
    {
        Activation,
        Ensure,
        Retry,
    }

    public enum FrozenContinuation
    {
        CaughtUp,
        Released,
    }

    public enum InvalidReleaseFence
    {
        WrongPublisher,
        WrongWriter,
        NotDrained,
    }

    public enum MissingLifecyclePort
    {
        Runtime,
        Dispatch,
    }

    public enum InvalidReleaseConfirmation
    {
        WrongPublisher,
        WrongWriter,
        WrongSource,
        WrongEpoch,
        BelowDrain,
    }

    public enum InvalidCaughtUpContinuation
    {
        WrongPublisher,
        WrongWriter,
        WrongSource,
        WrongEpoch,
    }

    [Fact]
    public void LegacyStatusShadowKind_IsTheRegisteredMaterializationScopeKindOfTheStatusContext()
    {
        LegacyStatusShadowKind.Should().Be(
            "projection.materialization-scope.projection-scope-status-materialization-context");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoRoute_WithoutQuiescence_RemainsLegacyAndRetriesRegardlessOfLiveV2Admission(
        bool hasLiveAdmission)
    {
        var harness = SourceScopeHarness.Build(
            admission: hasLiveAdmission ? CreateAdmission() : null,
            quiescence: null,
            registerCallbackScheduler: true);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            ActorCreate(LegacyStatusShadowKind, LegacyActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RetryTimeout(TestScopeAgent.RetryDelays[0]),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ]);
        harness.Agent.State.StatusRoute.Should().BeNull(
            "Phase A never starts a route, including under a fresh V2 OPEN admission");
        harness.Streams.Relays.Keys.Should().Equal((RootActorId, SourceScopeActorId));
        harness.Callbacks!.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task NoRoute_WithHistoricalQuiescence_StopsBridgeRetryButNeverCreatesARoute()
    {
        var evidence = CreateQuiescenceEvidence();
        var harness = SourceScopeHarness.Build(
            admission: null,
            quiescence: evidence,
            registerCallbackScheduler: true);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeStartedEvent.Descriptor),
            ActorCreate(LegacyStatusShadowKind, LegacyActorId),
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
            Commit(ProjectionObservationAttachmentUpdatedEvent.Descriptor),
        ]);
        harness.Agent.State.StatusRoute.Should().BeNull();
        harness.Callbacks!.Timeouts.Should().BeEmpty();
        harness.Fleet!.Quiescence.Should().BeEquivalentTo(evidence);
    }

    [Fact]
    public async Task HistoricalQuiescence_IsNotLiveAdmissionForTheQuiescedContract()
    {
        var evidence = CreateQuiescenceEvidence();
        var harness = SourceScopeHarness.Build(admission: null, quiescence: evidence);

        var receipt = await Aevatar.Foundation.Core.Runtime.RuntimeFleetCapabilityAdmissionValidation
            .GetQuiescenceReceiptAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion,
                harness.Fleet!);
        var grant = await GetLiveAdmissionGrantAsync(harness);

        receipt.Should().NotBeNull("historical completion evidence survives membership evolution");
        grant.Should().BeNull("a quiescence receipt is not live admission for the retired V2 gate");
        receipt!.Evidence.Should().BeEquivalentTo(evidence);
    }

    [Fact]
    public async Task RetryWithoutQuiescence_ReEnsuresLegacyAndBacksOffWithoutCreatingARoute()
    {
        var harness = SourceScopeHarness.Build(
            quiescence: null,
            registerCallbackScheduler: true);
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        var retry = harness.Callbacks!.Timeouts.Single().TriggerEnvelope.Payload!
            .Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>();
        harness.Journal.Clear();

        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(retry);

        harness.Journal.Should().Equal(
        [
            Dispatch(EnsureProjectionScopeCommand.Descriptor, LegacyActorId),
            RetryTimeout(TestScopeAgent.RetryDelays[1]),
        ]);
        harness.Agent.State.StatusRoute.Should().BeNull();
        harness.EventSourcing.Committed.Should().NotContain(
            static evt => evt is ProjectionScopeStatusRouteWarmingStartedEvent);
    }

    [Theory]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Warming, FrozenRoutePath.Activation)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Warming, FrozenRoutePath.Ensure)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Warming, FrozenRoutePath.Retry)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Blocked, FrozenRoutePath.Activation)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Blocked, FrozenRoutePath.Ensure)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Blocked, FrozenRoutePath.Retry)]
    [InlineData(FrozenRouteKind.Legacy, ProjectionScopeStatusRoutePhase.Warming, FrozenRoutePath.Activation)]
    [InlineData(FrozenRouteKind.Legacy, ProjectionScopeStatusRoutePhase.Blocked, FrozenRoutePath.Retry)]
    public async Task RouteWithoutQuiescence_ActivationEnsureAndRetry_OnlyScheduleTheBridgeRetry(
        FrozenRouteKind routeKind,
        ProjectionScopeStatusRoutePhase phase,
        FrozenRoutePath path)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = routeKind == FrozenRouteKind.Terminal
            ? ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(7, phase)
            : ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(7, phase);
        state.StatusRoute.WarmStartedVersion = 11;
        state.StatusRoute.BlockedVersion = phase == ProjectionScopeStatusRoutePhase.Blocked ? 13 : 0;
        var originalRoute = state.StatusRoute.Clone();
        var harness = SourceScopeHarness.Build(
            quiescence: null,
            initialState: state,
            registerCallbackScheduler: true);

        switch (path)
        {
            case FrozenRoutePath.Activation:
                await harness.Agent.ActivateForTestAsync();
                break;
            case FrozenRoutePath.Ensure:
                await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
                break;
            case FrozenRoutePath.Retry:
                await harness.Agent.HandleRetryStatusRouteAdoptionAsync(
                    new RetryProjectionScopeStatusRouteAdoptionCommand { Attempt = 3 });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }

        harness.Journal.Should().ContainSingle(entry => entry.StartsWith("timeout:", StringComparison.Ordinal));
        harness.Journal.Should().NotContain(entry => entry.StartsWith("dispatch:", StringComparison.Ordinal));
        harness.Journal.Should().NotContain(entry => entry.StartsWith("actor-create:", StringComparison.Ordinal));
        harness.Journal.Should().NotContain(entry => entry == RelayUpsert(SourceScopeActorId, TerminalActorId));
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeEquivalentTo(originalRoute);
        harness.Callbacks!.Timeouts.Should().ContainSingle().Which.DueTime.Should().Be(
            TestScopeAgent.RetryDelays[path == FrozenRoutePath.Retry ? 3 : 0]);
    }

    [Theory]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Warming, FrozenContinuation.CaughtUp)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Warming, FrozenContinuation.Released)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Blocked, FrozenContinuation.CaughtUp)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Blocked, FrozenContinuation.Released)]
    [InlineData(FrozenRouteKind.Legacy, ProjectionScopeStatusRoutePhase.Warming, FrozenContinuation.CaughtUp)]
    [InlineData(FrozenRouteKind.Legacy, ProjectionScopeStatusRoutePhase.Blocked, FrozenContinuation.Released)]
    public async Task RouteWithoutQuiescence_QueuedCaughtUpAndReleasedContinuations_AreNoOps(
        FrozenRouteKind routeKind,
        ProjectionScopeStatusRoutePhase phase,
        FrozenContinuation continuation)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = routeKind == FrozenRouteKind.Terminal
            ? ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(7, phase)
            : ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(7, phase);
        state.StatusRoute.WarmStartedVersion = 4;
        state.StatusRoute.BlockedVersion = phase == ProjectionScopeStatusRoutePhase.Blocked ? 5 : 0;
        var originalRoute = state.StatusRoute.Clone();
        var expectedWriterActorId = routeKind == FrozenRouteKind.Terminal
            ? continuation == FrozenContinuation.CaughtUp ? TerminalActorId : LegacyActorId
            : continuation == FrozenContinuation.CaughtUp ? LegacyActorId : TerminalActorId;
        var harness = SourceScopeHarness.Build(
            initialState: state,
            registerCallbackScheduler: true);

        if (continuation == FrozenContinuation.CaughtUp)
        {
            await DispatchContinuationAsync(harness.Agent, expectedWriterActorId,
                new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = expectedWriterActorId,
                RouteEpoch = 7,
                ObservedVersion = 99,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
        }
        else
        {
            await DispatchContinuationAsync(harness.Agent, expectedWriterActorId,
                new ProjectionScopeStatusWriterReleasedEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = expectedWriterActorId,
                RouteEpoch = 7,
                LastObservedVersion = 99,
                ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
        }

        harness.Journal.Should().BeEmpty();
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeEquivalentTo(originalRoute);
    }

    [Fact]
    public async Task QuiescedWarmingRoute_ReprobesAndRequiresAFreshAuthenticatedCaughtUpReport()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            7,
            ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 11;
        state.StatusRoute.CaughtUpVersion = 99;
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true);

        await harness.Agent.ActivateForTestAsync();

        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming,
            "pre-receipt caught-up state is not a bridge proof");
        var probe = harness.EventSourcing.Committed.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusRouteWarmingProbedEvent>().Subject;
        probe.RequiredObservedVersion.Should().Be(100);
        harness.Agent.State.StatusRoute.WarmStartedVersion.Should().Be(11,
            "the original cutover start retains its single meaning");
        harness.Agent.State.StatusRoute.WarmingProbeVersion.Should().Be(100);
        harness.Agent.State.StatusRoute.CaughtUpVersion.Should().Be(0);
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId));

        await DispatchContinuationAsync(harness.Agent, TerminalActorId,
            new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = TerminalActorId,
                RouteEpoch = 7,
                ObservedVersion = 99,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });

        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming,
            "an exact-writer report queued before the fresh probe has not observed its fence");
        harness.EventSourcing.Committed.Should().ContainSingle();

        await DispatchContinuationAsync(harness.Agent, TerminalActorId,
            new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = TerminalActorId,
                RouteEpoch = 7,
                ObservedVersion = probe.RequiredObservedVersion,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });

        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        var release = harness.DispatchPort.Dispatched
            .Select(static item => item.command.Payload)
            .Where(static payload => payload?.Is(ReleaseProjectionScopeCommand.Descriptor) == true)
            .Select(static payload => payload!.Unpack<ReleaseProjectionScopeCommand>())
            .Should().ContainSingle().Subject;
        release.ExpectedWriterActorId.Should().Be(LegacyActorId);
        release.RequiredObservedVersion.Should().Be(harness.Agent.State.StatusRoute.DrainProbeVersion);
    }

    [Fact]
    public async Task QuiescedWarmingRoute_RetryKeepsFenceSoDelayedCaughtUpReportCanProgress()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            7,
            ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 11;
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true);

        await harness.Agent.ActivateForTestAsync();
        var firstFence = harness.Agent.State.StatusRoute!.WarmingProbeVersion;

        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(
            new RetryProjectionScopeStatusRouteAdoptionCommand { Attempt = 1 });

        harness.EventSourcing.Committed
            .OfType<ProjectionScopeStatusRouteWarmingProbedEvent>()
            .Should().HaveCount(2).And.OnlyContain(evt => evt.RequiredObservedVersion == firstFence);
        harness.Agent.State.StatusRoute.WarmingProbeVersion.Should().Be(firstFence);

        await DispatchContinuationAsync(harness.Agent, TerminalActorId,
            new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = TerminalActorId,
                RouteEpoch = 7,
                ObservedVersion = firstFence,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });

        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked,
            "a response for the first durable probe fence remains valid after a retry");
    }

    [Theory]
    [InlineData(FrozenRouteKind.Terminal, InvalidCaughtUpContinuation.WrongPublisher)]
    [InlineData(FrozenRouteKind.Terminal, InvalidCaughtUpContinuation.WrongWriter)]
    [InlineData(FrozenRouteKind.Terminal, InvalidCaughtUpContinuation.WrongSource)]
    [InlineData(FrozenRouteKind.Terminal, InvalidCaughtUpContinuation.WrongEpoch)]
    [InlineData(FrozenRouteKind.Legacy, InvalidCaughtUpContinuation.WrongPublisher)]
    [InlineData(FrozenRouteKind.Legacy, InvalidCaughtUpContinuation.WrongWriter)]
    [InlineData(FrozenRouteKind.Legacy, InvalidCaughtUpContinuation.WrongSource)]
    [InlineData(FrozenRouteKind.Legacy, InvalidCaughtUpContinuation.WrongEpoch)]
    public async Task QuiescedWarmingRoute_InvalidCaughtUpContinuation_LeavesRouteUnchanged(
        FrozenRouteKind routeKind,
        InvalidCaughtUpContinuation invalidContinuation)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = routeKind == FrozenRouteKind.Terminal
            ? ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(7, ProjectionScopeStatusRoutePhase.Warming)
            : ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(7, ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 11;
        state.StatusRoute.WarmingProbeVersion = 12;
        var originalRoute = state.StatusRoute.Clone();
        var expectedWriterActorId = routeKind == FrozenRouteKind.Terminal ? TerminalActorId : LegacyActorId;
        var continuation = new ProjectionScopeStatusWriterCaughtUpEvent
        {
            SourceScopeActorId = invalidContinuation == InvalidCaughtUpContinuation.WrongSource
                ? "other-source"
                : SourceScopeActorId,
            WriterActorId = invalidContinuation == InvalidCaughtUpContinuation.WrongWriter
                ? "other-writer"
                : expectedWriterActorId,
            RouteEpoch = invalidContinuation == InvalidCaughtUpContinuation.WrongEpoch ? 8 : 7,
            ObservedVersion = 12,
            ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        var publisherActorId = invalidContinuation == InvalidCaughtUpContinuation.WrongPublisher
            ? "other-writer"
            : expectedWriterActorId;
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true);

        await DispatchContinuationAsync(harness.Agent, publisherActorId, continuation);

        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeEquivalentTo(originalRoute);
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Removed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FrozenRouteKind.Terminal, MissingLifecyclePort.Runtime)]
    [InlineData(FrozenRouteKind.Terminal, MissingLifecyclePort.Dispatch)]
    [InlineData(FrozenRouteKind.Legacy, MissingLifecyclePort.Runtime)]
    [InlineData(FrozenRouteKind.Legacy, MissingLifecyclePort.Dispatch)]
    public async Task QuiescedWarmingRoute_CaughtUpWithoutRequiredLifecyclePort_FailsClosedBeforeBlocking(
        FrozenRouteKind routeKind,
        MissingLifecyclePort missingPort)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = routeKind == FrozenRouteKind.Terminal
            ? ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(7, ProjectionScopeStatusRoutePhase.Warming)
            : ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(7, ProjectionScopeStatusRoutePhase.Warming);
        state.StatusRoute.WarmStartedVersion = 11;
        state.StatusRoute.WarmingProbeVersion = 12;
        var originalRoute = state.StatusRoute.Clone();
        var expectedWriterActorId = routeKind == FrozenRouteKind.Terminal ? TerminalActorId : LegacyActorId;
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true,
            registerActorRuntime: missingPort != MissingLifecyclePort.Runtime,
            registerDispatchPort: missingPort != MissingLifecyclePort.Dispatch);

        await DispatchContinuationAsync(harness.Agent, expectedWriterActorId,
            new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = expectedWriterActorId,
                RouteEpoch = 7,
                ObservedVersion = 12,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });

        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeEquivalentTo(originalRoute);
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Removed.Should().BeEmpty();
        harness.Callbacks!.Timeouts.Should().ContainSingle();
    }

    [Theory]
    [InlineData(FrozenRouteKind.Terminal)]
    [InlineData(FrozenRouteKind.Legacy)]
    public async Task QuiescedBlockedRoute_RepairsBothWritersAndRequiresExactFreshDrain(
        FrozenRouteKind routeKind)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = routeKind == FrozenRouteKind.Terminal
            ? ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(7, ProjectionScopeStatusRoutePhase.Blocked)
            : ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(7, ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.WarmStartedVersion = 11;
        state.StatusRoute.BlockedVersion = 13;
        state.StatusRoute.LegacyRouteReleased = true;
        var candidateWriterActorId = routeKind == FrozenRouteKind.Terminal ? TerminalActorId : LegacyActorId;
        var previousWriterActorId = routeKind == FrozenRouteKind.Terminal ? LegacyActorId : TerminalActorId;
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true);

        await harness.Agent.ActivateForTestAsync();

        harness.Runtime.ExistingActorIds.Should().Contain(candidateWriterActorId).And.Contain(previousWriterActorId);
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, candidateWriterActorId));
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, previousWriterActorId));
        harness.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeFalse();
        harness.Agent.State.StatusRoute.BlockedVersion.Should().Be(13);
        harness.Agent.State.StatusRoute.DrainProbeVersion.Should().Be(14);
        harness.EventSourcing.Committed.Should().ContainSingle(static evt =>
            evt is ProjectionScopeStatusRouteDrainProbedEvent);

        var release = harness.DispatchPort.Dispatched
            .Select(static item => item.command.Payload)
            .Where(static payload => payload?.Is(ReleaseProjectionScopeCommand.Descriptor) == true)
            .Select(static payload => payload!.Unpack<ReleaseProjectionScopeCommand>())
            .Should().ContainSingle().Subject;
        release.ExpectedWriterActorId.Should().Be(previousWriterActorId);
        release.RequiredObservedVersion.Should().Be(14);

        await DispatchContinuationAsync(harness.Agent, previousWriterActorId,
            new ProjectionScopeStatusWriterReleasedEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = previousWriterActorId,
                RouteEpoch = 7,
                LastObservedVersion = 13,
                ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);

        await DispatchContinuationAsync(harness.Agent, previousWriterActorId,
            new ProjectionScopeStatusWriterReleasedEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = previousWriterActorId,
                RouteEpoch = 7,
                LastObservedVersion = 14,
                ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });

        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, candidateWriterActorId));
        harness.Streams.Relays.Should().NotContainKey((SourceScopeActorId, previousWriterActorId));
    }

    [Fact]
    public async Task QuiescedBlockedRoute_RetryKeepsDrainFenceSoDelayedConfirmationCanActivate()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            7,
            ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.BlockedVersion = 13;
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true);

        await harness.Agent.ActivateForTestAsync();
        var firstDrainFence = harness.Agent.State.StatusRoute!.DrainProbeVersion;

        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(
            new RetryProjectionScopeStatusRouteAdoptionCommand { Attempt = 1 });

        harness.EventSourcing.Committed
            .OfType<ProjectionScopeStatusRouteDrainProbedEvent>()
            .Should().HaveCount(2).And.OnlyContain(evt => evt.RequiredObservedVersion == firstDrainFence);
        harness.Agent.State.StatusRoute.BlockedVersion.Should().Be(13);
        harness.Agent.State.StatusRoute.DrainProbeVersion.Should().Be(firstDrainFence);
        harness.DispatchPort.Dispatched
            .Select(static item => item.command.Payload)
            .Where(static payload => payload?.Is(ReleaseProjectionScopeCommand.Descriptor) == true)
            .Select(static payload => payload!.Unpack<ReleaseProjectionScopeCommand>())
            .Should().HaveCount(2).And.OnlyContain(command =>
                command.RequiredObservedVersion == firstDrainFence);

        await DispatchContinuationAsync(harness.Agent, LegacyActorId,
            new ProjectionScopeStatusWriterReleasedEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = LegacyActorId,
                RouteEpoch = 7,
                LastObservedVersion = firstDrainFence,
                ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });

        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active,
            "a confirmation for the first durable drain fence remains valid after a retry");
    }

    [Theory]
    [InlineData(MissingLifecyclePort.Runtime)]
    [InlineData(MissingLifecyclePort.Dispatch)]
    public async Task QuiescedBlockedRoute_WithoutRequiredLifecyclePort_FailsClosed(
        MissingLifecyclePort missingPort)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            7,
            ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.BlockedVersion = 13;
        var originalRoute = state.StatusRoute.Clone();
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true,
            registerActorRuntime: missingPort != MissingLifecyclePort.Runtime,
            registerDispatchPort: missingPort != MissingLifecyclePort.Dispatch);

        await harness.Agent.ActivateForTestAsync();

        harness.Agent.State.StatusRoute.Should().BeEquivalentTo(originalRoute);
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Relays.Should().NotContainKey((SourceScopeActorId, TerminalActorId));
        harness.Streams.Relays.Should().NotContainKey((SourceScopeActorId, LegacyActorId));
        harness.Callbacks!.Timeouts.Should().ContainSingle();
    }

    [Theory]
    [InlineData(InvalidReleaseConfirmation.WrongPublisher)]
    [InlineData(InvalidReleaseConfirmation.WrongWriter)]
    [InlineData(InvalidReleaseConfirmation.WrongSource)]
    [InlineData(InvalidReleaseConfirmation.WrongEpoch)]
    [InlineData(InvalidReleaseConfirmation.BelowDrain)]
    public async Task QuiescedBlockedRoute_InvalidReleaseConfirmation_LeavesRouteAndRelayBlocked(
        InvalidReleaseConfirmation invalidConfirmation)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            7,
            ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.BlockedVersion = 13;
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true);
        await harness.Agent.ActivateForTestAsync();
        var requiredObservedVersion = harness.Agent.State.StatusRoute!.DrainProbeVersion;
        var confirmation = new ProjectionScopeStatusWriterReleasedEvent
        {
            SourceScopeActorId = invalidConfirmation == InvalidReleaseConfirmation.WrongSource
                ? "other-source"
                : SourceScopeActorId,
            WriterActorId = invalidConfirmation == InvalidReleaseConfirmation.WrongWriter
                ? "other-writer"
                : LegacyActorId,
            RouteEpoch = invalidConfirmation == InvalidReleaseConfirmation.WrongEpoch ? 8 : 7,
            LastObservedVersion = invalidConfirmation == InvalidReleaseConfirmation.BelowDrain
                ? requiredObservedVersion - 1
                : requiredObservedVersion,
            ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        var publisherActorId = invalidConfirmation == InvalidReleaseConfirmation.WrongPublisher
            ? "other-writer"
            : LegacyActorId;

        await DispatchContinuationAsync(harness.Agent, publisherActorId, confirmation);

        harness.EventSourcing.Committed.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusRouteDrainProbedEvent>();
        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeFalse();
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, LegacyActorId));
        harness.Streams.Removed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FrozenRouteKind.Terminal)]
    [InlineData(FrozenRouteKind.Legacy)]
    public async Task QuiescedBlockedRoute_WhenPreviousRelayRemovalFails_CommitsNothingAndRetries(
        FrozenRouteKind routeKind)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = routeKind == FrozenRouteKind.Terminal
            ? ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(7, ProjectionScopeStatusRoutePhase.Blocked)
            : ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(7, ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.BlockedVersion = 13;
        var previousWriterActorId = routeKind == FrozenRouteKind.Terminal ? LegacyActorId : TerminalActorId;
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: state,
            registerCallbackScheduler: true);
        await harness.Agent.ActivateForTestAsync();
        var requiredObservedVersion = harness.Agent.State.StatusRoute!.DrainProbeVersion;
        var confirmation = new ProjectionScopeStatusWriterReleasedEvent
        {
            SourceScopeActorId = SourceScopeActorId,
            WriterActorId = previousWriterActorId,
            RouteEpoch = 7,
            LastObservedVersion = requiredObservedVersion,
            ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        harness.Streams.RemoveRelayFailure = new IOException("relay store unavailable");

        var failedConfirmation = () => DispatchContinuationAsync(
            harness.Agent,
            previousWriterActorId,
            confirmation);
        await failedConfirmation.Should().ThrowAsync<IOException>();

        harness.EventSourcing.Committed.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusRouteDrainProbedEvent>();
        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeFalse();
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, previousWriterActorId));

        harness.Streams.RemoveRelayFailure = null;
        await DispatchContinuationAsync(harness.Agent, previousWriterActorId, confirmation);

        harness.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        harness.Streams.Relays.Should().NotContainKey((SourceScopeActorId, previousWriterActorId));
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    public async Task ExistingTerminalWriter_HealsOnlyItsRelayAndNeverRollsBackOrDispatchesRelease(
        ProjectionScopeStatusRoutePhase phase)
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(5, phase);
        var harness = SourceScopeHarness.Build(
            admission: CreateAdmission(status: RuntimeFleetCapabilityGateStatus.Revoked),
            initialState: state,
            registerCallbackScheduler: true);

        await harness.Agent.ActivateForTestAsync();

        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            ActorCreate(ProjectionScopeStatusGAgent.AgentKind, TerminalActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.Agent.State.StatusRoute.Should().BeEquivalentTo(state.StatusRoute);
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Callbacks!.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviousTerminalContractWriter_RemainsActiveWithoutInPlaceUpgrade()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = BuildPreviousContractTerminalRoute(4);
        var harness = SourceScopeHarness.Build(admission: CreateAdmission(), initialState: state);

        await harness.Agent.ActivateForTestAsync();

        harness.Agent.State.StatusRoute.Should().BeEquivalentTo(state.StatusRoute);
        harness.Journal.Should().Equal(
        [
            RelayUpsert(SourceScopeActorId, TerminalActorId),
            ActorCreate(ProjectionScopeStatusGAgent.AgentKind, TerminalActorId),
            RelayUpsert(RootActorId, SourceScopeActorId),
        ]);
        harness.EventSourcing.Committed.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionObservationScope_NeverOwnsAStatusRoute()
    {
        var sessionScopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            RootActorId,
            ProjectionKind,
            ProjectionRuntimeMode.SessionObservation,
            "session-alpha"));
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            runtimeMode: ProjectionRuntimeMode.SessionObservation,
            scopeActorId: sessionScopeActorId);

        await harness.Agent.HandleEnsureAsync(new EnsureProjectionScopeCommand
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            SessionId = "session-alpha",
            Mode = ProjectionScopeMode.SessionObservation,
        });

        harness.Agent.State.StatusRoute.Should().BeNull();
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Relays.Keys.Should().OnlyContain(key => key.Source == RootActorId);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusMaterializationContext.ProjectionKindValue)]
    [InlineData(ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue)]
    public async Task StatusWriterScope_NeverOwnsAStatusRouteOrCreatesAnotherWriter(string statusKind)
    {
        var statusScopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            SourceScopeActorId,
            statusKind,
            ProjectionRuntimeMode.DurableMaterialization));
        var harness = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            registerCallbackScheduler: true,
            scopeActorId: statusScopeActorId);

        await harness.Agent.HandleEnsureAsync(new EnsureProjectionScopeCommand
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = statusKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
        });

        harness.Agent.State.StatusRoute.Should().BeNull();
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Callbacks!.Timeouts.Should().BeEmpty();
        harness.Streams.Relays.Keys.Should().Equal((SourceScopeActorId, statusScopeActorId));
    }

    [Fact]
    public async Task BlockedRoute_ObservationFailureIsRetryableAndLeavesNoDurableTrace()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 3,
            ProjectionScopeStatusRoutePhase.Blocked);
        var harness = SourceScopeHarness.Build(initialState: state);
        var envelope = BuildForwardedRootEnvelope(BuildSourceStateAtVersion(3, 10), 3);

        var failure = (await harness.Agent.Invoking(agent => agent.HandleObservedEnvelopeAsync(envelope))
            .Should().ThrowExactlyAsync<ProjectionScopeStatusRouteBlockedException>()).Which;

        failure.Should().BeAssignableTo<Aevatar.Foundation.Abstractions.IRuntimeEnvelopeRetryableException>();
        failure.RouteEpoch.Should().Be(3);
        harness.Journal.Should().BeEmpty();
        harness.Agent.State.ReceivedEnvelopeTotal.Should().Be(0);
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(0);
    }

    [Fact]
    public async Task HandleReplayAsync_WhileRouteBlocked_RefusesDispatchWithoutSpendingReplayBudget()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 7,
            ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.BlockedVersion = 13;
        state.StatusRoute.DrainProbeVersion = 14;
        state.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = "failure-1",
            Stage = "dispatch",
            EventId = "source-evt-2",
            EventType = ProjectionScopeWatermarkAdvancedEvent.Descriptor.FullName,
            SourceVersion = 2,
            SourceActorId = RootActorId,
            Reason = "boom",
            Envelope = BuildForwardedRootEnvelope(BuildSourceStateAtVersion(2, 10), 2),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });
        var harness = SourceScopeHarness.Build(
            quiescence: null,
            initialState: state,
            registerCallbackScheduler: true);
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        harness.EventSourcing.Committed.Clear();

        await harness.Agent.HandleReplayAsync(new ReplayProjectionFailuresCommand { MaxItems = 1 });

        harness.Journal.Should().BeEmpty();
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(0);
        harness.Agent.State.Failures.Should().ContainSingle().Which.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task HandleResumeInFlightObservationAsync_WhileRouteBlocked_RefusesDispatch()
    {
        var inFlightSource = new ProjectionSourceCoordinate
        {
            ActorId = RootActorId,
            StateVersion = 3,
            EventId = "source-evt-3",
        };
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 7,
            ProjectionScopeStatusRoutePhase.Blocked);
        state.StatusRoute.BlockedVersion = 13;
        state.StatusRoute.DrainProbeVersion = 14;
        state.InFlightObservation = new ProjectionScopeInFlightObservation
        {
            Source = inFlightSource,
            Envelope = BuildForwardedRootEnvelope(BuildSourceStateAtVersion(3, 10), 3),
            EventKind = ProjectionScopeWatermarkAdvancedEvent.Descriptor.FullName,
            StagedAtUtc = Timestamp.FromDateTimeOffset(Now),
        };
        var harness = SourceScopeHarness.Build(
            initialState: state,
            enablesDurableObservationRecovery: true);

        var resume = () => harness.Agent.HandleResumeInFlightObservationAsync(
            new ResumeProjectionInFlightObservationCommand
            {
                ExpectedSource = inFlightSource.Clone(),
            });

        var blocked = (await resume.Should().ThrowExactlyAsync<ProjectionScopeStatusRouteBlockedException>()).Which;
        blocked.ScopeActorId.Should().Be(SourceScopeActorId);
        blocked.RouteEpoch.Should().Be(7);
        harness.Journal.Should().BeEmpty();
        harness.EventSourcing.Committed.Should().BeEmpty();
        harness.Agent.State.AttemptedEnvelopeTotal.Should().Be(0);
        harness.Agent.State.InFlightObservation.Should().NotBeNull();
    }

    [Fact]
    public async Task QuiescedWarmingSource_RealLegacyReleaseRoundTrip_ActivatesOnlyAfterDurableDrainConfirmation()
    {
        var sourceState = BuildActiveSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 7,
            ProjectionScopeStatusRoutePhase.Warming);
        sourceState.StatusRoute.WarmStartedVersion = 11;
        var source = SourceScopeHarness.Build(
            quiescence: CreateQuiescenceEvidence(),
            initialState: sourceState,
            registerCallbackScheduler: true);

        await source.Agent.ActivateForTestAsync();
        var warmingProbe = source.EventSourcing.Committed
            .OfType<ProjectionScopeStatusRouteWarmingProbedEvent>()
            .Should().ContainSingle().Subject;

        await DispatchContinuationAsync(source.Agent, TerminalActorId,
            new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = TerminalActorId,
                RouteEpoch = 7,
                ObservedVersion = warmingProbe.RequiredObservedVersion,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });

        source.Agent.State.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        var drainProbe = source.EventSourcing.Committed
            .OfType<ProjectionScopeStatusRouteDrainProbedEvent>()
            .Should().ContainSingle().Subject;
        var release = source.DispatchPort.Dispatched
            .Select(static item => item.command.Payload)
            .Where(static payload => payload?.Is(ReleaseProjectionScopeCommand.Descriptor) == true)
            .Select(static payload => payload!.Unpack<ReleaseProjectionScopeCommand>())
            .Should().ContainSingle().Subject;
        release.RequiredObservedVersion.Should().Be(drainProbe.RequiredObservedVersion);
        release.ExpectedWriterActorId.Should().Be(LegacyActorId);

        var legacy = BuildLegacyShadowHarness(quiescence: CreateQuiescenceEvidence());
        await legacy.Agent.ActivateForTestAsync();
        await DispatchReleaseAsync(legacy.Agent, release);
        legacy.Agent.State.Released.Should().BeFalse(
            "inbox acceptance cannot replace the forwarded committed drain publication");

        await legacy.Agent.HandleObservedEnvelopeAsync(BuildCommittedSourceEnvelope(
            LegacyActorId,
            source.Agent.State,
            source.EventSourcing.CurrentVersion));

        legacy.Agent.State.Released.Should().BeTrue();
        legacy.Agent.State.ReleasedAtObservedVersion.Should().Be(source.EventSourcing.CurrentVersion);
        legacy.EventSourcing.Committed.Should().ContainSingle(static evt => evt is ProjectionScopeReleasedEvent);
        var confirmation = legacy.Publisher.SentTo.Should().ContainSingle().Which;
        confirmation.TargetActorId.Should().Be(SourceScopeActorId);
        var released = confirmation.Event.Should().BeOfType<ProjectionScopeStatusWriterReleasedEvent>().Subject;
        released.LastObservedVersion.Should().BeGreaterThanOrEqualTo(drainProbe.RequiredObservedVersion);

        await DispatchContinuationAsync(source.Agent, LegacyActorId, released);

        source.Agent.State.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        source.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeTrue();
        source.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId));
        source.Streams.Relays.Should().NotContainKey((SourceScopeActorId, LegacyActorId));
        source.Streams.Removed.Should().Contain((SourceScopeActorId, LegacyActorId));
        source.EventSourcing.Committed.Should().ContainSingle(static evt =>
            evt is ProjectionScopeStatusLegacyRouteReleasedEvent);
        source.EventSourcing.Committed.Should().ContainSingle(static evt =>
            evt is ProjectionScopeStatusRouteActivatedEvent);
    }

    [Theory]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Warming, false)]
    [InlineData(FrozenRouteKind.Terminal, ProjectionScopeStatusRoutePhase.Blocked, true)]
    [InlineData(FrozenRouteKind.Legacy, ProjectionScopeStatusRoutePhase.Warming, false)]
    [InlineData(FrozenRouteKind.Legacy, ProjectionScopeStatusRoutePhase.Blocked, true)]
    public async Task LegacyWriter_ObservingFrozenRoute_NeverEmitsAContinuationOrReleases(
        FrozenRouteKind routeKind,
        ProjectionScopeStatusRoutePhase phase,
        bool skipsObservation)
    {
        var harness = BuildLegacyShadowHarness();
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        var sourceState = BuildSourceStateAtVersion(3, 10);
        sourceState.StatusRoute = routeKind == FrozenRouteKind.Terminal
            ? ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(8, phase)
            : ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(8, phase);

        await harness.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(LegacyActorId, sourceState, 3));

        harness.Publisher.SentTo.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeFalse();
        if (skipsObservation)
            harness.Journal.Should().BeEmpty();
        else
            harness.Journal.Should().Equal(
                Commit(ProjectionScopeEnvelopeReceivedEvent.Descriptor),
                Commit(ProjectionScopeEnvelopeAttemptedEvent.Descriptor));
    }

    [Fact]
    public async Task LegacyCandidate_AfterQuiescence_ReportsFreshRollbackWarmingObservation()
    {
        var harness = BuildLegacyShadowHarness(quiescence: CreateQuiescenceEvidence());
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        var sourceState = BuildSourceStateAtVersion(3, 10);
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(
            8,
            ProjectionScopeStatusRoutePhase.Warming);

        await harness.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(LegacyActorId, sourceState, 3));

        harness.Publisher.SentTo.Should().ContainSingle().Which.Event
            .Should().BeEquivalentTo(new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = LegacyActorId,
                RouteEpoch = 8,
                ObservedVersion = 3,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Fact]
    public async Task LegacyPreviousWriter_AfterQuiescence_DurablyReleasesOnForwardedBlockedProbe()
    {
        var harness = BuildLegacyShadowHarness(quiescence: CreateQuiescenceEvidence());
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        var sourceState = BuildSourceStateAtVersion(14, 20);
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            8,
            ProjectionScopeStatusRoutePhase.Blocked);
        sourceState.StatusRoute.BlockedVersion = 13;
        sourceState.StatusRoute.DrainProbeVersion = 14;

        await harness.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(LegacyActorId, sourceState, 14));

        harness.Agent.State.Released.Should().BeTrue();
        harness.Agent.State.ReleasedAtObservedVersion.Should().Be(14);
        harness.EventSourcing.Committed.Should().ContainSingle(static evt =>
            evt is ProjectionScopeReleasedEvent);
        harness.Publisher.SentTo.Should().ContainSingle().Which.Event
            .Should().BeEquivalentTo(new ProjectionScopeStatusWriterReleasedEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = LegacyActorId,
                RouteEpoch = 8,
                LastObservedVersion = 14,
                ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
    }

    [Theory]
    [InlineData(InvalidReleaseFence.WrongPublisher)]
    [InlineData(InvalidReleaseFence.WrongWriter)]
    [InlineData(InvalidReleaseFence.NotDrained)]
    public async Task LegacyWriter_StatusRouteRelease_FailsClosedUnlessPublisherWriterAndDrainMatch(
        InvalidReleaseFence invalidFence)
    {
        var harness = BuildLegacyShadowHarness(highestSeenVersion: 12);
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();
        var command = BuildLegacyShadowReleaseCommand(
            statusRouteEpoch: 3,
            requiredObservedVersion: invalidFence == InvalidReleaseFence.NotDrained ? 13 : 12);
        if (invalidFence == InvalidReleaseFence.WrongWriter)
            command.ExpectedWriterActorId = TerminalActorId;

        await DispatchReleaseAsync(
            harness.Agent,
            command,
            invalidFence == InvalidReleaseFence.WrongPublisher ? "other-source" : SourceScopeActorId);

        harness.Journal.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeFalse();
        harness.Publisher.SentTo.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyWriter_StatusRouteRelease_CommitsDrainBeforeAuthenticConfirmation()
    {
        var harness = BuildLegacyShadowHarness(highestSeenVersion: 12);
        await harness.Agent.ActivateForTestAsync();
        harness.Journal.Clear();

        await DispatchReleaseAsync(
            harness.Agent,
            BuildLegacyShadowReleaseCommand(statusRouteEpoch: 3, requiredObservedVersion: 12));

        harness.Journal.Should().Equal(
        [
            Commit(ProjectionScopeReleasedEvent.Descriptor),
            RelayRemove(SourceScopeActorId, LegacyActorId),
            SendTo(ProjectionScopeStatusWriterReleasedEvent.Descriptor, SourceScopeActorId),
        ]);
        harness.Agent.State.ReleasedAtObservedVersion.Should().Be(12);
        harness.Publisher.SentTo.Should().ContainSingle().Which.Event
            .Should().BeEquivalentTo(new ProjectionScopeStatusWriterReleasedEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = LegacyActorId,
                RouteEpoch = 3,
                LastObservedVersion = 12,
                ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now),
            });
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
        warming.WarmingProbeVersion = 6;
        warming.DrainProbeVersion = 7;
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
        next.StatusRoute.WarmingProbeVersion.Should().Be(0);
        next.StatusRoute.DrainProbeVersion.Should().Be(0);
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

    [Fact]
    public void Transition_WarmingProbeForCurrentRoute_AdvancesDedicatedFenceAndClearsOldCaughtUpProof()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 1,
            ProjectionScopeStatusRoutePhase.Warming);
        current.StatusRoute.WarmStartedVersion = 10;
        current.StatusRoute.WarmingProbeVersion = 11;
        current.StatusRoute.CaughtUpVersion = 99;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteWarmingProbedEvent
        {
            RouteEpoch = 1,
            RequiredObservedVersion = 12,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.WarmingProbeVersion.Should().Be(11);
        current.StatusRoute.CaughtUpVersion.Should().Be(99);
        next.StatusRoute!.WarmStartedVersion.Should().Be(10);
        next.StatusRoute.WarmingProbeVersion.Should().Be(12);
        next.StatusRoute.CaughtUpVersion.Should().Be(0);
    }

    [Theory]
    [InlineData(2, ProjectionScopeStatusRoutePhase.Warming, 12)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Active, 12)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Warming, 11)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Warming, 9)]
    public void Transition_StaleOrInvalidWarmingProbe_LeavesRouteUnchanged(
        long eventEpoch,
        ProjectionScopeStatusRoutePhase currentPhase,
        long requiredObservedVersion)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 1,
            currentPhase);
        current.StatusRoute.WarmStartedVersion = 10;
        current.StatusRoute.WarmingProbeVersion = 11;
        current.StatusRoute.CaughtUpVersion = 99;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteWarmingProbedEvent
        {
            RouteEpoch = eventEpoch,
            RequiredObservedVersion = requiredObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute!.WarmingProbeVersion.Should().Be(11);
        next.StatusRoute.CaughtUpVersion.Should().Be(99);
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
        current.StatusRoute.DrainProbeVersion = 99;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteBlockedEvent
        {
            RouteEpoch = 1,
            BlockedVersion = 12,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().NotBeSameAs(current);
        next.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Blocked);
        next.StatusRoute.RouteEpoch.Should().Be(1);
        next.StatusRoute.BlockedVersion.Should().Be(12);
        next.StatusRoute.DrainProbeVersion.Should().Be(0);
    }

    [Fact]
    public void Transition_DrainProbeForCurrentBlockedRoute_RecordsDedicatedFenceAndPreservesBlockedVersion()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 1,
            ProjectionScopeStatusRoutePhase.Blocked);
        current.StatusRoute.BlockedVersion = 10;
        current.StatusRoute.DrainProbeVersion = 11;
        current.StatusRoute.LegacyRouteReleased = true;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteDrainProbedEvent
        {
            RouteEpoch = 1,
            RequiredObservedVersion = 12,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.BlockedVersion.Should().Be(10);
        current.StatusRoute.DrainProbeVersion.Should().Be(11);
        current.StatusRoute.LegacyRouteReleased.Should().BeTrue();
        next.StatusRoute!.BlockedVersion.Should().Be(10);
        next.StatusRoute.DrainProbeVersion.Should().Be(12);
        next.StatusRoute.LegacyRouteReleased.Should().BeFalse();
    }

    [Theory]
    [InlineData(2, ProjectionScopeStatusRoutePhase.Blocked, 12)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Active, 12)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Blocked, 11)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Blocked, 10)]
    public void Transition_StaleOrInvalidDrainProbe_LeavesRouteUnchanged(
        long eventEpoch,
        ProjectionScopeStatusRoutePhase currentPhase,
        long requiredObservedVersion)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 1,
            currentPhase);
        current.StatusRoute.BlockedVersion = 10;
        current.StatusRoute.DrainProbeVersion = 11;
        current.StatusRoute.LegacyRouteReleased = true;

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteDrainProbedEvent
        {
            RouteEpoch = eventEpoch,
            RequiredObservedVersion = requiredObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute!.BlockedVersion.Should().Be(10);
        next.StatusRoute.DrainProbeVersion.Should().Be(11);
        next.StatusRoute.LegacyRouteReleased.Should().BeTrue();
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

    [Theory]
    [InlineData(4)]
    [InlineData(3)]
    public void Transition_ContractUpgradedWithoutAHigherEpoch_LeavesRouteUnchanged(long eventEpoch)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = BuildPreviousContractTerminalRoute(routeEpoch: 4);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteContractUpgradedEvent
        {
            Route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(eventEpoch),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current, "the epoch fence rejects a stale or replayed upgrade outright");
        next.StatusRoute!.ContractId.Should().Be(PreviousTerminalContractId);
        next.StatusRoute.RouteEpoch.Should().Be(4);
    }

    [Fact]
    public void Transition_ContractUpgradedOverACutoverInFlight_LeavesRouteUnchanged()
    {
        // A route being moved to another writer is not upgraded in place under it.
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = BuildPreviousContractTerminalRoute(routeEpoch: 4, ProjectionScopeStatusRoutePhase.Warming);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteContractUpgradedEvent
        {
            Route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 5),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute!.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Warming);
        next.StatusRoute.ContractId.Should().Be(PreviousTerminalContractId);
    }

    [Fact]
    public void Transition_ContractUpgradedOverALegacyRoute_LeavesRouteUnchanged()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(routeEpoch: 4);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteContractUpgradedEvent
        {
            Route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 5),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current, "an in-place contract upgrade never changes which writer the route selects");
        ProjectionScopeStatusRoutePolicy.IsLegacyRoute(next.StatusRoute).Should().BeTrue();
    }

    [Fact]
    public void Transition_ContractUpgradedAtAHigherEpochOnAWritingRoute_ReplacesTheRouteAsActive()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = BuildPreviousContractTerminalRoute(routeEpoch: 4);
        current.StatusRoute.LegacyRouteReleased = true;
        // The event may not smuggle a non-writing phase through.
        var upgraded = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            routeEpoch: 5,
            ProjectionScopeStatusRoutePhase.Warming);
        upgraded.LegacyRouteReleased = true;
        upgraded.FlipVersion = 9;
        var occurredAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(1));

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteContractUpgradedEvent
        {
            Route = upgraded,
            OccurredAtUtc = occurredAt,
        });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.RouteEpoch.Should().Be(4, "appliers never mutate the current state in place");
        next.StatusRoute!.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2);
        next.StatusRoute.ContractVersion.Should().Be(2);
        next.StatusRoute.RouteEpoch.Should().Be(5);
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        next.StatusRoute.LegacyRouteReleased.Should().BeTrue();
        next.StatusRoute.FlipVersion.Should().Be(9);
        next.UpdatedAtUtc.Should().Be(occurredAt);
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

    /// <summary>The legacy status shadow of <see cref="SourceScopeActorId"/> as a scope actor under test.</summary>
    private static SourceScopeHarness BuildLegacyShadowHarness(
        long highestSeenVersion = 0,
        RuntimeFleetCapabilityQuiescenceEvidence? quiescence = null)
    {
        var state = BuildActiveLegacyShadowState();
        state.HighestSeenVersion = highestSeenVersion;
        return SourceScopeHarness.Build(
            quiescence: quiescence,
            registerFleetReaders: quiescence != null,
            registerCallbackScheduler: true,
            scopeActorId: LegacyActorId,
            agentKind: LegacyStatusShadowKind,
            initialState: state);
    }

    /// <summary>The release the source dispatches to the legacy shadow of a cutover at this epoch.</summary>
    private static ReleaseProjectionScopeCommand BuildLegacyShadowReleaseCommand(
        long statusRouteEpoch,
        long requiredObservedVersion = 1) =>
        new()
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
            Mode = ProjectionScopeMode.DurableMaterialization,
            StatusRouteEpoch = statusRouteEpoch,
            ExpectedWriterActorId = statusRouteEpoch > 0 ? LegacyActorId : string.Empty,
            RequiredObservedVersion = statusRouteEpoch > 0 ? requiredObservedVersion : 0,
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

    /// <summary>
    /// A terminal route as an older binary committed it: the PREVIOUS terminal contract (v1),
    /// which this binary's materializer still serves but never creates a new route under.
    /// </summary>
    private static ProjectionScopeStatusRoute BuildPreviousContractTerminalRoute(
        long routeEpoch,
        ProjectionScopeStatusRoutePhase phase = ProjectionScopeStatusRoutePhase.Active) =>
        new()
        {
            ContractId = PreviousTerminalContractId,
            ContractVersion = PreviousTerminalContractVersion,
            RouteEpoch = routeEpoch,
            Phase = phase,
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

    /// <summary>A fresh live grant fixture. Phase A deliberately does not consume it.</summary>
    private static RuntimeFleetCapabilityAdmission CreateAdmission(
        RuntimeFleetCapability capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
        RuntimeFleetCapabilityGateStatus status = RuntimeFleetCapabilityGateStatus.Open,
        string contractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2,
        DateTimeOffset? validUntil = null,
        int minimumReaderContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion)
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
            MinimumReaderContractVersion = minimumReaderContractVersion,
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
            QuiescenceTransitionId = "transition:quiesce-v2",
        };

    private static Task<RuntimeFleetCapabilityAdmissionGrant?> GetLiveAdmissionGrantAsync(
        SourceScopeHarness harness) =>
        Aevatar.Foundation.Core.Runtime.RuntimeFleetCapabilityAdmissionValidation.GetGrantedAdmissionAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            ProjectionScopeStatusGAgent.ContractId,
            (int)ProjectionScopeStatusGAgent.ContractVersion,
            harness.Fleet!,
            harness.Fleet!,
            new FixedTimeProvider(Now));

    private static Task DispatchReleaseAsync(
        TestScopeAgent agent,
        ReleaseProjectionScopeCommand command,
        string? publisherActorId = null) =>
        agent.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId ?? SourceScopeActorId, LegacyActorId),
            Payload = Any.Pack(command),
        });

    private static Task DispatchContinuationAsync<TEvent>(
        TestScopeAgent agent,
        string publisherActorId,
        TEvent evt)
        where TEvent : IMessage =>
        agent.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, SourceScopeActorId),
            Payload = Any.Pack(evt),
        });

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
            RuntimeFleetCapabilityQuiescenceEvidence? quiescence = null,
            bool registerFleetReaders = true,
            bool registerActorRuntime = true,
            bool registerDispatchPort = true,
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
            var initialVersion = Math.Max(
                initialState?.StatusRoute?.BlockedVersion ?? 0,
                Math.Max(
                    initialState?.StatusRoute?.DrainProbeVersion ?? 0,
                    Math.Max(
                        initialState?.StatusRoute?.WarmingProbeVersion ?? 0,
                        Math.Max(
                            initialState?.StatusRoute?.WarmStartedVersion ?? 0,
                            initialState?.StatusRoute?.CaughtUpVersion ?? 0))));
            var eventSourcing = new TrackingEventSourcing<ProjectionScopeState>(
                agent.Transition,
                journal,
                initialVersion);
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
            if (registerActorRuntime)
                services.AddSingleton<IActorRuntime>(runtime);
            if (registerDispatchPort)
                services.AddSingleton<IActorDispatchPort>(dispatchPort);

            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            MutableFleetAdmissionSource? fleet = null;
            if (registerFleetReaders)
            {
                fleet = new MutableFleetAdmissionSource(admission, quiescence, membership);
                services.AddSingleton<IRuntimeFleetCapabilityAdmissionReader>(fleet);
                services.AddSingleton<IRuntimeFleetCapabilityQuiescenceReader>(fleet);
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

        public TrackingEventSourcing(
            Func<TState, IMessage, TState> transition,
            List<string> journal,
            long initialVersion = 0)
        {
            _transition = transition;
            _journal = journal;
            CurrentVersion = initialVersion;
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

    /// <summary>
    /// Live OPEN admission and historical QUIESCED evidence are deliberately separate views.
    /// </summary>
    private sealed class MutableFleetAdmissionSource
        : IRuntimeFleetCapabilityAdmissionReader,
            IRuntimeFleetCapabilityQuiescenceReader,
            IRuntimeLocalMembershipIdentityReader
    {
        private readonly Dictionary<RuntimeFleetCapability, RuntimeFleetCapabilityAdmission> _admissions = [];

        public MutableFleetAdmissionSource(
            RuntimeFleetCapabilityAdmission? admission,
            RuntimeFleetCapabilityQuiescenceEvidence? quiescence,
            RuntimeLocalMembershipIdentity? membership)
        {
            Admission = admission;
            Quiescence = quiescence?.Clone();
            Membership = membership ?? new RuntimeLocalMembershipIdentity(7, "digest-a", "revision-a", "member-a", "inc-a");
        }

        /// <summary>The single published gate; assigning replaces whatever was published before.</summary>
        public RuntimeFleetCapabilityAdmission? Admission
        {
            get => _admissions.Values.FirstOrDefault();
            set
            {
                _admissions.Clear();
                Publish(value);
            }
        }

        public RuntimeLocalMembershipIdentity? Membership { get; set; }

        public RuntimeFleetCapabilityQuiescenceEvidence? Quiescence { get; set; }

        public void Publish(RuntimeFleetCapabilityAdmission? admission)
        {
            if (admission != null)
                _admissions[admission.Capability] = admission;
        }

        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                _admissions.TryGetValue(capability, out var admission) ? admission.Clone() : null);
        }

        public Task<RuntimeFleetCapabilityQuiescenceEvidence?> GetQuiescenceAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                Quiescence?.Capability == capability ? Quiescence.Clone() : null);
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
