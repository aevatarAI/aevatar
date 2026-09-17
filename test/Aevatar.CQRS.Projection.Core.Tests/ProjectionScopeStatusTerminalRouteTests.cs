using System.Reflection;
using System.Text;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// Behavior of the terminal single-hop status materializer (#3476): one status document
/// write per terminal source outcome, no per-envelope bookkeeping, durable backed-off
/// deferred-write retry through the callback scheduler, and the source-owned phased status
/// route (warming -> blocked -> active, with a legacy rollback route) that decides who may
/// write and whose epoch fences same-version document takeovers.
///
/// A rejected write (Conflict/Gap) never advances delivery: on the observed path the
/// observation FAILS so the provider redelivers it without advancing its checkpoint (nothing
/// is persisted); on the durable retry path — where the envelope is already this actor's own
/// fact — the pending write stays durably retryable at the capped cadence. A previous-writer
/// release is confirmed to the source with a typed continuation only after it is committed,
/// and the materializer serves routes of the previous terminal contract but never of a later
/// one, so a mixed fleet fails closed in both directions.
/// </summary>
public sealed class ProjectionScopeStatusTerminalRouteTests
{
    public enum InvalidSealRequest
    {
        WrongRole,
        WrongActorId,
        WrongAgentKind,
        WrongRouteEpoch,
        WrongPublisher,
        MissingRuntimeSource,
        WrongRuntimeSource,
        NonDirect,
        MissingAdoptionReceipt,
    }

    private const string RootActorId = "root-actor";
    private const string ProjectionKind = "test-kind";
    private const string TerminalWriteAlertStage = "terminal-status-write";

    /// <summary>
    /// The terminal contract an older source binary committed its routes under. This binary
    /// creates no new route under it but still serves the ones that exist.
    /// </summary>
    private const string PreviousTerminalContractId =
        RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1;

    private const long PreviousTerminalContractVersion =
        1;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan CappedRetryDelay = TimeSpan.FromMinutes(10);

    private static readonly string SourceScopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
        RootActorId,
        ProjectionKind,
        ProjectionRuntimeMode.DurableMaterialization));

    private static readonly string TerminalActorId =
        ProjectionScopeStatusRoutes.BuildTerminalActorId(SourceScopeActorId);

    private static readonly string LegacyActorId =
        ProjectionScopeStatusRoutes.BuildLegacyActorId(SourceScopeActorId);

    private static readonly string LegacyWriterAgentKind = ProjectionScopeAgentRegistration
        .Create<ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>>()
        .Kind;

    // ─── 1. Ensure lifecycle ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleEnsureAsync_ShouldStartAndBindRelayOnSourceStream()
    {
        var harness = new TerminalHarness();

        await harness.Agent.HandleEnsureAsync(BuildEnsureCommand());

        var started = harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>().Subject;
        started.SourceScopeActorId.Should().Be(SourceScopeActorId);
        started.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        started.ContractVersion.Should().Be(ProjectionScopeStatusGAgent.ContractVersion);
        started.ActivationGeneration.Should().Be(1);
        started.OccurredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(FixedNow));

        harness.Agent.State.Active.Should().BeTrue();
        harness.Agent.State.Released.Should().BeFalse();
        harness.Agent.State.SourceScopeActorId.Should().Be(SourceScopeActorId);
        harness.Agent.State.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);

        var binding = await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId);
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                binding,
                SourceScopeActorId,
                TerminalActorId,
                ProjectionScopeStatusGAgent.AgentKind)
            .Should().BeTrue("the relay on the SOURCE stream is the durable activation evidence");
        harness.Streams.StreamIds.Should().Equal(SourceScopeActorId);
        harness.Dispatcher.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleStatusActorSealRequestAsync_ExactRequest_RepliesWithRuntimeOwnedSeal()
    {
        var harness = await TerminalHarness.CreateStartedAsync(phaseBReady: true);

        await DispatchSealRequestAsync(
            harness.Agent,
            BuildSealRequest(
                ProjectionScopeStatusActorRole.TerminalWriter,
                TerminalActorId,
                ProjectionScopeStatusGAgent.AgentKind,
                routeEpoch: 5),
            SourceScopeActorId);

        var sent = harness.Outbox.Sent.Should().ContainSingle().Which;
        sent.TargetActorId.Should().Be(SourceScopeActorId);
        var ready = sent.Message.Should().BeOfType<ProjectionScopeStatusActorSealReadyEvent>().Subject;
        ready.SourceScopeActorId.Should().Be(SourceScopeActorId);
        ready.RouteEpoch.Should().Be(5);
        ready.Seal.Should().BeEquivalentTo(CreateActivationSeal(
            ProjectionScopeStatusActorRole.TerminalWriter,
            TerminalActorId,
            ProjectionScopeStatusGAgent.AgentKind));
    }

    [Theory]
    [InlineData(InvalidSealRequest.WrongRole)]
    [InlineData(InvalidSealRequest.WrongActorId)]
    [InlineData(InvalidSealRequest.WrongAgentKind)]
    [InlineData(InvalidSealRequest.WrongRouteEpoch)]
    [InlineData(InvalidSealRequest.WrongPublisher)]
    [InlineData(InvalidSealRequest.MissingRuntimeSource)]
    [InlineData(InvalidSealRequest.WrongRuntimeSource)]
    [InlineData(InvalidSealRequest.NonDirect)]
    [InlineData(InvalidSealRequest.MissingAdoptionReceipt)]
    public async Task HandleStatusActorSealRequestAsync_InvalidRequest_DoesNotReply(
        InvalidSealRequest invalidRequest)
    {
        var harness = await TerminalHarness.CreateStartedAsync(
            phaseBReady: invalidRequest != InvalidSealRequest.MissingAdoptionReceipt);
        var command = BuildSealRequest(
            invalidRequest == InvalidSealRequest.WrongRole
                ? ProjectionScopeStatusActorRole.LegacyWriter
                : ProjectionScopeStatusActorRole.TerminalWriter,
            invalidRequest == InvalidSealRequest.WrongActorId ? "other-writer" : TerminalActorId,
            invalidRequest == InvalidSealRequest.WrongAgentKind
                ? "other.kind"
                : ProjectionScopeStatusGAgent.AgentKind,
            invalidRequest == InvalidSealRequest.WrongRouteEpoch ? 0 : 5);

        await DispatchSealRequestAsync(
            harness.Agent,
            command,
            invalidRequest == InvalidSealRequest.WrongPublisher ? "other-source" : SourceScopeActorId,
            includeRuntimeSource: invalidRequest != InvalidSealRequest.MissingRuntimeSource,
            runtimeSourceActorId: invalidRequest == InvalidSealRequest.WrongRuntimeSource
                ? "other-runtime-source"
                : null,
            direct: invalidRequest != InvalidSealRequest.NonDirect);

        harness.Outbox.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    public async Task HandleObservedEnvelopeAsync_FreshPhaseBWithoutThreeSeals_RemainsSilent(
        ProjectionScopeStatusRoutePhase phase)
    {
        var harness = await TerminalHarness.CreateStartedAsync(
            quiescence: CreateQuiescenceEvidence(),
            phaseBReady: true);
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(5, phase);
        sourceState.StatusRoute.BlockedVersion = phase == ProjectionScopeStatusRoutePhase.Blocked ? 12 : 0;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(
                version: 12,
                eventId: "evt-no-seals",
                new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.Outbox.Sent.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    public async Task HandleObservedEnvelopeAsync_BoundPhaseBWithUnavailableLiveProof_RequestsRedelivery(
        ProjectionScopeStatusRoutePhase phase)
    {
        var harness = await TerminalHarness.CreateStartedAsync(
            quiescence: null,
            phaseBReady: true);
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(5, phase);
        sourceState.StatusRoute.BlockedVersion = phase == ProjectionScopeStatusRoutePhase.Blocked ? 12 : 0;
        AddPhaseBSeals(sourceState.StatusRoute);
        var envelope = BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(
                version: 12,
                eventId: "evt-bound-live-proof-unavailable",
                new ProjectionScopeWatermarkAdvancedEvent()));

        Func<Task> act = () => harness.Agent.HandleObservedEnvelopeAsync(envelope);

        var exception = await act.Should()
            .ThrowAsync<ProjectionScopeStatusPhaseBProofUnavailableException>();
        exception.Which.MaterializerActorId.Should().Be(TerminalActorId);
        exception.Which.SourceScopeActorId.Should().Be(SourceScopeActorId);
        exception.Which.RouteEpoch.Should().Be(5);
        exception.Which.Phase.Should().Be(phase);
        exception.Which.WriterRole.Should().Be(ProjectionScopeStatusActorRole.TerminalWriter);
        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.Outbox.Sent.Should().BeEmpty();

        var recovered = await TerminalHarness.CreateStartedAsync(
            quiescence: CreateQuiescenceEvidence(),
            phaseBReady: true);
        await recovered.Agent.HandleObservedEnvelopeAsync(envelope);
        if (phase == ProjectionScopeStatusRoutePhase.Warming)
        {
            recovered.Outbox.Sent.Should().ContainSingle(message =>
                message.Message is ProjectionScopeStatusWriterCaughtUpEvent);
        }
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenAlreadyStarted_ShouldBeIdempotent()
    {
        var harness = new TerminalHarness();
        await harness.Agent.HandleEnsureAsync(BuildEnsureCommand());

        await harness.Agent.HandleEnsureAsync(BuildEnsureCommand());

        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.ActivationGeneration.Should().Be(1);
        harness.Streams.GetStream(SourceScopeActorId).UpsertCount.Should().Be(2, "the relay is re-asserted, not re-started");
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId))!
            .ActivationGeneration.Should().Be(1);
    }

    [Fact]
    public async Task HandleEnsureAsync_WithWrongProjectionKind_ShouldThrow()
    {
        var harness = new TerminalHarness();
        var command = BuildEnsureCommand();
        command.ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue;

        var act = () => harness.Agent.HandleEnsureAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>();
        harness.EventSourcing.PersistedEvents.Should().BeEmpty();
        harness.Streams.StreamIds.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithSessionMode_ShouldThrow()
    {
        var harness = new TerminalHarness();
        var command = BuildEnsureCommand();
        command.Mode = ProjectionScopeMode.SessionObservation;

        var act = () => harness.Agent.HandleEnsureAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>();
        harness.EventSourcing.PersistedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithSessionId_ShouldThrow()
    {
        var harness = new TerminalHarness();
        var command = BuildEnsureCommand();
        command.SessionId = "session-1";

        var act = () => harness.Agent.HandleEnsureAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>();
        harness.EventSourcing.PersistedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithoutSourceScopeActorId_ShouldThrow()
    {
        var harness = new TerminalHarness();
        var command = BuildEnsureCommand();
        command.RootActorId = " ";

        var act = () => harness.Agent.HandleEnsureAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── 2. Routed terminal outcome → exactly one write, zero bookkeeping ────

    [Fact]
    public async Task HandleObservedEnvelopeAsync_RoutedTerminalOutcome_ShouldWriteExactlyOnceWithoutBookkeeping()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildRoutedSourceState();
        sourceState.LastSuccessfulVersion = 41;
        sourceState.HighestSeenVersion = 42;
        var stateEvent = BuildStateEvent(version: 42, eventId: "evt-42", new ProjectionScopeWatermarkAdvancedEvent());

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(sourceState, stateEvent));

        var document = harness.Dispatcher.Documents.Should().ContainSingle().Subject;
        document.Id.Should().Be(SourceScopeActorId);
        document.ScopeActorId.Should().Be(SourceScopeActorId);
        document.StateVersion.Should().Be(42);
        document.LastEventId.Should().Be("evt-42");
        document.LastSuccessfulVersion.Should().Be(41);
        document.HighestSeenVersion.Should().Be(42);
        document.UpdatedAtUtcValue.Should().Be(stateEvent.Timestamp);
        harness.EventSourcing.PersistedEvents.Should().ContainSingle(
            "the terminal materializer keeps no per-envelope bookkeeping stream")
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Outbox.Published.Should().BeEmpty();
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Agent.State.PendingWrite.Should().BeNull();
    }

    [Theory]
    [InlineData(typeof(ProjectionScopeDispatchFailedEvent))]
    [InlineData(typeof(ProjectionScopeStartedEvent))]
    [InlineData(typeof(ProjectionScopeReleasedEvent))]
    [InlineData(typeof(ProjectionScopeStatusRouteActivatedEvent))]
    public async Task HandleObservedEnvelopeAsync_OtherRoutedTerminalOutcomes_ShouldWriteOnce(System.Type sourceEventType)
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceEvent = (IMessage)Activator.CreateInstance(sourceEventType)!;
        var sourceState = BuildRoutedSourceState();
        sourceState.ObservationAttached = true;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 7, eventId: "evt-7", sourceEvent)));

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(7);
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
    }

    // ─── 3. Intermediate outcomes → no write, no event ───────────────────────

    [Theory]
    [InlineData(typeof(ProjectionScopeEnvelopeReceivedEvent))]
    [InlineData(typeof(ProjectionScopeEnvelopeAttemptedEvent))]
    [InlineData(typeof(ProjectionScopeObservationStagedEvent))]
    public async Task HandleObservedEnvelopeAsync_IntermediateOutcome_ShouldNeitherWriteNorPersist(System.Type sourceEventType)
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceEvent = (IMessage)Activator.CreateInstance(sourceEventType)!;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 3, eventId: "evt-3", sourceEvent)));

        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Outbox.Published.Should().BeEmpty();
    }

    // ─── 4. No / foreign route → observe only ────────────────────────────────

    [Fact]
    public async Task HandleObservedEnvelopeAsync_NoRoute_ShouldObserveOnly()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = null;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().BeEmpty("the legacy shadow is still the writer");
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
        harness.Outbox.Sent.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ForeignContract_ShouldObserveOnly()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = new ProjectionScopeStatusRoute
        {
            ContractId = "aevatar.projection.scope-status-other.v1",
            ContractVersion = ProjectionScopeStatusGAgent.ContractVersion,
            RouteEpoch = 1,
        };

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    public async Task HandleObservedEnvelopeAsync_NewerContractVersion_ShouldObserveOnly(ProjectionScopeStatusRoutePhase phase)
    {
        // A terminal route committed for a contract version newer than this reader names a
        // writer this binary does not implement: observe only, report nothing.
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase);
        sourceState.StatusRoute.ContractVersion = ProjectionScopeStatusGAgent.ContractVersion + 1;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.Outbox.Sent.Should().BeEmpty();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_NotYetActive_NewerContractVersion_ShouldNotSelfStart()
    {
        var harness = new TerminalHarness();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, ProjectionScopeStatusRoutePhase.Warming);
        sourceState.StatusRoute.ContractVersion = ProjectionScopeStatusGAgent.ContractVersion + 1;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.EventSourcing.PersistedEvents.Should().BeEmpty();
        harness.Agent.State.Active.Should().BeFalse();
        harness.Outbox.Sent.Should().BeEmpty();
    }

    // ─── 4b. Phase-A terminal routes: frozen cutovers, active writers preserved ──

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ActiveTerminalRoute_ShouldWriteOncePerTerminalOutcome()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            2,
            ProjectionScopeStatusRoutePhase.Active);
        sourceState.StatusRoute.ContractVersion.Should().Be(ProjectionScopeStatusGAgent.ContractVersion,
            "a built terminal route names the current reader version");

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 6, eventId: "evt-6", new ProjectionScopeEnvelopeReceivedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 7, eventId: "evt-7", new ProjectionScopeWatermarkAdvancedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 8, eventId: "evt-8", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Select(static document => document.StateVersion).Should().Equal([7L, 8L],
            "only terminal outcomes are written, once each; the intermediate bookkeeping fact is not");
        harness.Dispatcher.Documents.Should().OnlyContain(document =>
            document.StatusRoute.Equals(sourceState.StatusRoute) &&
            ((IProjectionRouteFencedReadModel)document).RouteEpoch == 2);
        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Outbox.Sent.Should().BeEmpty("a writing phase reports nothing to the source");
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_PhaselessV1TerminalRoute_ShouldStillWrite()
    {
        // A route adopted by the previous binary: contract version 1, no phase (phase-less
        // routes are ACTIVE by definition), epoch 1. Any reader at or above version 1 (i.e.
        // every reader) keeps writing it.
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = new ProjectionScopeStatusRoute
        {
            ContractId = ProjectionScopeStatusGAgent.ContractId,
            ContractVersion = 1,
            RouteEpoch = 1,
            Phase = ProjectionScopeStatusRoutePhase.Unspecified,
        };

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 9, eventId: "evt-9", new ProjectionScopeWatermarkAdvancedEvent())));

        var document = harness.Dispatcher.Documents.Should().ContainSingle().Subject;
        document.StateVersion.Should().Be(9);
        document.StatusRoute.Should().Be(sourceState.StatusRoute);
        ((IProjectionRouteFencedReadModel)document).RouteEpoch.Should().Be(1);
        harness.Outbox.Sent.Should().BeEmpty();
        harness.Agent.State.PendingWrite.Should().BeNull();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_NotYetActive_PhaselessV1TerminalRoute_ShouldSelfStartAndWrite()
    {
        var harness = new TerminalHarness();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = new ProjectionScopeStatusRoute
        {
            ContractId = ProjectionScopeStatusGAgent.ContractId,
            ContractVersion = 1,
            RouteEpoch = 1,
        };

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 9, eventId: "evt-9", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.Active.Should().BeTrue();
        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(9);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    public async Task HandleObservedEnvelopeAsync_FrozenTerminalRoute_NotYetActive_ShouldNotStartOrContinueCutover(
        ProjectionScopeStatusRoutePhase phase)
    {
        var harness = new TerminalHarness();
        harness.Agent.State.Active.Should().BeFalse();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(3, phase);
        sourceState.StatusRoute.WarmStartedVersion = 10;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 10, eventId: "evt-10", new ProjectionScopeStatusRouteWarmingStartedEvent())));

        harness.EventSourcing.PersistedEvents.Should().BeEmpty();
        harness.Agent.State.Active.Should().BeFalse();
        harness.Dispatcher.UpsertAttempts.Should().Be(0);
        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.Outbox.Sent.Should().BeEmpty();
        harness.Outbox.Published.Should().BeEmpty();
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Alerts.Published.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    public async Task HandleObservedEnvelopeAsync_FrozenTerminalRoute_ShouldNotWriteReleaseOrContinueCutover(
        ProjectionScopeStatusRoutePhase phase)
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, phase);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 11, eventId: "evt-11", new ProjectionScopeWatermarkAdvancedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeEnvelopeReceivedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 13, eventId: "evt-13", new ProjectionScopeStatusRouteWarmingProbedEvent())));

        harness.Outbox.Sent.Should().BeEmpty();
        harness.Dispatcher.UpsertAttempts.Should().Be(0);
        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked)]
    public async Task HandleObservedEnvelopeAsync_PreviousContractFrozenRoute_ShouldNotWriteReleaseOrContinueCutover(
        ProjectionScopeStatusRoutePhase phase)
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = BuildPreviousContractTerminalRoute(4, phase);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 46, eventId: "evt-46", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.UpsertAttempts.Should().Be(0);
        harness.Outbox.Sent.Should().BeEmpty();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    public async Task HandleObservedEnvelopeAsync_PreviousContractTerminalRouteInWritingPhase_ShouldStillWrite(
        ProjectionScopeStatusRoutePhase phase)
    {
        // A route committed under the PREVIOUS terminal contract by an older source binary: this
        // materializer serves it, so the source keeps its status writer across the upgrade.
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = BuildPreviousContractTerminalRoute(4, phase);
        sourceState.StatusRoute.ContractId.Should().Be(PreviousTerminalContractId);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 45, eventId: "evt-45", new ProjectionScopeWatermarkAdvancedEvent())));

        var document = harness.Dispatcher.Documents.Should().ContainSingle().Subject;
        document.StateVersion.Should().Be(45);
        document.StatusRoute.Should().Be(sourceState.StatusRoute, "the document carries the source's committed route");
        ((IProjectionRouteFencedReadModel)document).RouteEpoch.Should().Be(4);
        harness.Outbox.Sent.Should().BeEmpty("a writing phase reports nothing to the source");
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WarmingThenBlockedThenActive_ShouldFreezeUntilActive()
    {
        var harness = new TerminalHarness();
        var warming = BuildSourceState();
        warming.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, ProjectionScopeStatusRoutePhase.Warming);
        warming.StatusRoute.WarmStartedVersion = 20;
        var blocked = warming.Clone();
        blocked.StatusRoute.Phase = ProjectionScopeStatusRoutePhase.Blocked;
        blocked.StatusRoute.CaughtUpVersion = 21;
        var active = blocked.Clone();
        active.StatusRoute.Phase = ProjectionScopeStatusRoutePhase.Active;
        active.StatusRoute.FlipVersion = 23;
        active.StatusRoute.LegacyRouteReleased = true;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            warming,
            BuildStateEvent(version: 20, eventId: "evt-20", new ProjectionScopeStatusRouteWarmingStartedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            warming,
            BuildStateEvent(version: 21, eventId: "evt-21", new ProjectionScopeWatermarkAdvancedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            blocked,
            BuildStateEvent(version: 22, eventId: "evt-22", new ProjectionScopeStatusRouteBlockedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            active,
            BuildStateEvent(version: 23, eventId: "evt-23", new ProjectionScopeStatusRouteActivatedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            active,
            BuildStateEvent(version: 24, eventId: "evt-24", new ProjectionScopeEnvelopeReceivedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            active,
            BuildStateEvent(version: 25, eventId: "evt-25", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Outbox.Sent.Should().BeEmpty();
        harness.Dispatcher.Writes.Select(static write => (write.Version, write.RouteEpoch)).Should().Equal(
            (23, 2),
            (25, 2));
        harness.Dispatcher.Documents.Should().OnlyContain(document =>
            document.StatusRoute.Phase == ProjectionScopeStatusRoutePhase.Active &&
            document.StatusRoute.LegacyRouteReleased);
        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.PendingWrite.Should().BeNull();
    }

    // ─── 4c. Rollback: legacy route warming keeps us writing, blocked/active releases us ──

    [Fact]
    public async Task HandleObservedEnvelopeAsync_LegacyRouteWarming_ShouldKeepWritingTerminalOutcomes()
    {
        // Rollback in flight: the legacy shadow is warming under a legacy route; we remain the
        // writer until the source blocks the route.
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(3, ProjectionScopeStatusRoutePhase.Warming);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 30, eventId: "evt-30", new ProjectionScopeStatusRouteWarmingStartedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 31, eventId: "evt-31", new ProjectionScopeEnvelopeReceivedEvent())));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 32, eventId: "evt-32", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Writes.Select(static write => (write.Version, write.RouteEpoch)).Should().Equal((30, 3), (32, 3));
        harness.Dispatcher.Documents.Should().OnlyContain(document =>
            document.StatusRoute.ContractId == ProjectionScopeStatusRoutePolicy.LegacyContractId &&
            document.StatusRoute.Phase == ProjectionScopeStatusRoutePhase.Warming);
        harness.Outbox.Sent.Should().BeEmpty("only the legacy shadow reports during a rollback warming");
        harness.Agent.State.Released.Should().BeFalse();
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Active)]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified)]
    public async Task HandleObservedEnvelopeAsync_LegacyRouteInWritingPhase_ShouldReleaseWithoutWriting(
        ProjectionScopeStatusRoutePhase phase)
    {
        // A rollback that an older source already completed still names the legacy writer. The
        // terminal writer releases itself, but Phase A never emits the old cutover continuation.
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(3, phase);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 17, eventId: "evt-17", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.UpsertAttempts.Should().Be(0);
        harness.EventSourcing.PersistedEvents.Should().HaveCount(2);
        var released = harness.EventSourcing.PersistedEvents[1]
            .Should().BeOfType<ProjectionScopeStatusTerminalReleasedEvent>().Subject;
        released.OccurredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(FixedNow));
        released.LastObservedVersion.Should().Be(17,
            "the publication that rolled us back is the last one drained through the source's relay");
        harness.Agent.State.Released.Should().BeTrue();
        harness.Agent.State.ReleasedAtObservedVersion.Should().Be(17, "the drained version is a durable fact");
        harness.Agent.State.PendingWrite.Should().BeNull();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().BeNull();
        harness.Streams.GetStream(SourceScopeActorId).RemovedTargets.Should().Equal(TerminalActorId);
        harness.Outbox.Sent.Should().BeEmpty("Phase A never continues a V2 rollback");
        harness.Callbacks.Timeouts.Should().BeEmpty();

        // a straggler for the now-released source (the legacy writer owns it) is ignored
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 18, eventId: "evt-18", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.UpsertAttempts.Should().Be(0);
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore);
        harness.Agent.State.Released.Should().BeTrue();
        harness.Outbox.Sent.Should().BeEmpty();
        harness.Streams.GetStream(SourceScopeActorId).RemovedTargets.Should().Equal(
            new[] { TerminalActorId },
            "a released materializer does not touch the source stream again");
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_LegacyRouteBlocked_WithPendingWrite_ShouldFreezeAndPreservePending()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Agent.State.PendingWrite.Should().NotBeNull();
        var retry = UnpackRetryCommand(harness.Callbacks.Timeouts.Single());
        var rolledBack = BuildSourceState();
        rolledBack.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, ProjectionScopeStatusRoutePhase.Blocked);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            rolledBack,
            BuildStateEvent(version: 13, eventId: "evt-13", new ProjectionScopeStatusRouteBlockedEvent())));

        harness.Agent.State.Released.Should().BeFalse();
        harness.Agent.State.PendingWrite.Should().NotBeNull("Phase A leaves the current writer intact");
        harness.Outbox.Sent.Should().BeEmpty();

        await harness.Agent.HandleRetryWriteAsync(retry);

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(12);
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_LegacyRouteBlocked_AfterQuiescence_ShouldCommitDrainThenConfirm()
    {
        var harness = await TerminalHarness.CreateStartedAsync(
            quiescence: CreateQuiescenceEvidence(),
            phaseBReady: true);
        var rolledBack = BuildSourceState();
        rolledBack.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(
            4,
            ProjectionScopeStatusRoutePhase.Blocked);
        rolledBack.StatusRoute.BlockedVersion = 17;
        AddPhaseBSeals(rolledBack.StatusRoute);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            rolledBack,
            BuildStateEvent(version: 17, eventId: "evt-17", new ProjectionScopeStatusRouteDrainProbedEvent())));

        harness.Agent.State.Released.Should().BeTrue();
        harness.Agent.State.ReleasedAtObservedVersion.Should().Be(17);
        harness.EventSourcing.PersistedEvents[^1]
            .Should().BeOfType<ProjectionScopeStatusTerminalReleasedEvent>();
        harness.Outbox.ReleaseConfirmations.Should().ContainSingle()
            .Which.ReleaseConfirmation.Should().Be(new ProjectionScopeStatusWriterReleasedEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = TerminalActorId,
                RouteEpoch = 4,
                LastObservedVersion = 17,
                ReleasedAtUtc = Timestamp.FromDateTimeOffset(FixedNow),
            });
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_AfterLegacyRollbackRelease_RoutedTerminalPublicationOfLiveSource_ShouldRestart()
    {
        // Re-adoption after a rollback: the source warms a new terminal epoch, then activates it;
        // its routed publications restart the released materializer.
        var harness = await TerminalHarness.CreateStartedAsync();
        var rolledBack = BuildSourceState();
        rolledBack.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, ProjectionScopeStatusRoutePhase.Active);
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            rolledBack,
            BuildStateEvent(version: 40, eventId: "evt-40", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Agent.State.Released.Should().BeTrue();
        harness.Outbox.Sent.Should().BeEmpty();
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;

        var reWarming = BuildSourceState();
        reWarming.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(3, ProjectionScopeStatusRoutePhase.Warming);
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            reWarming,
            BuildStateEvent(version: 41, eventId: "evt-41", new ProjectionScopeStatusRouteWarmingStartedEvent())));

        harness.Agent.State.Released.Should().BeTrue();
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore);
        harness.Dispatcher.UpsertAttempts.Should().Be(0);
        harness.Outbox.Sent.Should().BeEmpty();

        var reActive = BuildSourceState();
        reActive.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(3, ProjectionScopeStatusRoutePhase.Active);
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            reActive,
            BuildStateEvent(version: 42, eventId: "evt-42", new ProjectionScopeStatusRouteActivatedEvent())));

        harness.Dispatcher.Writes.Should().ContainSingle().Which.Should().Be((42L, 3L, ProjectionWriteDisposition.Applied));
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore + 1);
        harness.EventSourcing.PersistedEvents[^1].Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>()
            .Which.ActivationGeneration.Should().Be(2);
        harness.Outbox.Sent.Should().BeEmpty();
        harness.Outbox.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_NotYetActive_LegacyRoute_ShouldNotSelfStart()
    {
        var harness = new TerminalHarness();
        foreach (var phase in new[]
                 {
                     ProjectionScopeStatusRoutePhase.Warming,
                     ProjectionScopeStatusRoutePhase.Blocked,
                     ProjectionScopeStatusRoutePhase.Active,
                     ProjectionScopeStatusRoutePhase.Unspecified,
                 })
        {
            var sourceState = BuildSourceState();
            sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, phase);

            await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
                sourceState,
                BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent())));
        }

        harness.EventSourcing.PersistedEvents.Should().BeEmpty("a legacy route never names the terminal writer");
        harness.Agent.State.Active.Should().BeFalse();
        harness.Dispatcher.UpsertAttempts.Should().Be(0);
        harness.Outbox.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_EnvelopeForAnotherSource_ShouldBeIgnored()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var otherState = BuildRoutedSourceState();
        otherState.RootActorId = "other-root";

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            otherState,
            BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.Agent.State.SourceScopeActorId.Should().Be(SourceScopeActorId);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_NotAddressedToTerminal_ShouldBeIgnored()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var envelope = BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent()),
            targetStreamId: "some-other-target");

        await harness.Agent.HandleObservedEnvelopeAsync(envelope);

        harness.Dispatcher.Documents.Should().BeEmpty();
    }

    // ─── 5. Cold self-start from a routed publication ────────────────────────

    [Fact]
    public async Task HandleObservedEnvelopeAsync_NotYetActive_RoutedEnvelope_ShouldSelfStartAndWrite()
    {
        var harness = new TerminalHarness();
        harness.Agent.State.Active.Should().BeFalse();

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 9, eventId: "evt-9", new ProjectionScopeWatermarkAdvancedEvent())));

        var started = harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>().Subject;
        started.SourceScopeActorId.Should().Be(SourceScopeActorId, "the source id is rebuilt from the observed state");
        harness.Agent.State.Active.Should().BeTrue();
        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(9);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_NotYetActive_UnroutedEnvelope_ShouldDoNothing()
    {
        var harness = new TerminalHarness();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = null;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 9, eventId: "evt-9", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.EventSourcing.PersistedEvents.Should().BeEmpty();
        harness.Agent.State.Active.Should().BeFalse();
        harness.Dispatcher.Documents.Should().BeEmpty();
    }

    // ─── 6. Deferred write + durable backed-off retry ────────────────────────

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenWriteThrows_ShouldDeferDurablyAndScheduleDurableRetry()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        var envelope = BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent()));

        await harness.Agent.HandleObservedEnvelopeAsync(envelope);

        var deferred = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>()
            .Should().ContainSingle().Subject;
        deferred.Pending.Source.Should().Be(new ProjectionSourceCoordinate
        {
            ActorId = SourceScopeActorId,
            StateVersion = 12,
            EventId = "evt-12",
        });
        deferred.Pending.Envelope.Should().Be(envelope);
        deferred.Pending.Attempts.Should().Be(1);
        deferred.Pending.LastError.Should().Be(nameof(IOException));
        deferred.Pending.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Transient);
        deferred.Pending.Stalled.Should().BeFalse();
        deferred.Pending.DeferredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(FixedNow));
        deferred.Pending.NextRetryAtUtc.Should().Be(
            Timestamp.FromDateTimeOffset(FixedNow + ProjectionScopeStatusGAgent.WriteRetryDelays[0]));
        harness.Agent.State.PendingWrite.Should().Be(deferred.Pending);

        var timeout = harness.Callbacks.Timeouts.Should().ContainSingle().Subject;
        timeout.ActorId.Should().Be(TerminalActorId);
        timeout.CallbackId.Should().Be(ProjectionScopeStatusGAgent.WriteRetryCallbackId);
        timeout.DueTime.Should().Be(ProjectionScopeStatusGAgent.WriteRetryDelays[0]);
        timeout.DeliveryMode.Should().Be(RuntimeCallbackDeliveryMode.FiredSelfEvent);
        var command = UnpackRetryCommand(timeout);
        command.Attempt.Should().Be(1);
        command.ExpectedSource.Should().Be(deferred.Pending.Source);
        harness.Outbox.Published.Should().BeEmpty("the retry is a durable callback, never an immediate self-publish");
        harness.Alerts.Published.Should().BeEmpty("a first transient failure is not an alert");
    }

    [Fact]
    public async Task HandleRetryWriteAsync_WhenDispatcherRecovers_ShouldWriteAndRecover()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        var retry = UnpackRetryCommand(harness.Callbacks.Timeouts.Single());

        await harness.Agent.HandleRetryWriteAsync(retry);

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(12);
        var recovered = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>()
            .Should().ContainSingle().Subject;
        recovered.Source.Should().Be(retry.ExpectedSource);
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().ContainSingle("no further retry is scheduled after recovery");
    }

    [Fact]
    public async Task HandleRetryWriteAsync_AfterInitialAndThreeFailedRetries_ShouldRecoverOnFourthRetryWithoutNewSourceEvent()
    {
        // The store is down for the initial write and three fired retries (attempts 1..4), each
        // deferral persisting and re-arming a longer durable timeout; then the store recovers and
        // the 4th fired retry writes the document. No new source event, no ensure command and no
        // activation is needed for that recovery.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        await harness.FireLatestRetryAsync();
        await harness.FireLatestRetryAsync();
        await harness.FireLatestRetryAsync();

        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.Dispatcher.UpsertAttempts.Should().Be(4);
        var deferrals = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>().ToList();
        deferrals.Select(static evt => evt.Pending.Attempts).Should().Equal(1, 2, 3, 4);
        deferrals.Should().OnlyContain(static evt =>
            evt.Pending.FailureKind == ProjectionScopeStatusWriteFailureKind.Transient && !evt.Pending.Stalled);
        deferrals.Select(static evt => evt.Pending.NextRetryAtUtc).Should().Equal(
            ProjectionScopeStatusGAgent.WriteRetryDelays.Take(4)
                .Select(static delay => Timestamp.FromDateTimeOffset(FixedNow + delay)));
        harness.Callbacks.Timeouts.Should().HaveCount(4);
        harness.Callbacks.Timeouts.Should().OnlyContain(static timeout =>
            timeout.CallbackId == ProjectionScopeStatusGAgent.WriteRetryCallbackId &&
            timeout.ActorId == TerminalActorId);
        harness.Callbacks.Timeouts.Select(static timeout => timeout.DueTime).Should().Equal(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2));
        harness.Callbacks.Timeouts.Select(timeout => UnpackRetryCommand(timeout).Attempt).Should().Equal(1, 2, 3, 4);
        harness.Callbacks.Timeouts.Should().OnlyContain(timeout =>
            UnpackRetryCommand(timeout).ExpectedSource.Equals(harness.Agent.State.PendingWrite!.Source));
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(4);

        harness.Dispatcher.Recover();
        await harness.FireLatestRetryAsync();

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(12);
        harness.Dispatcher.UpsertAttempts.Should().Be(5);
        var recovered = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>()
            .Should().ContainSingle().Subject;
        recovered.Source.StateVersion.Should().Be(12);
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().HaveCount(4, "no further timeout is scheduled after recovery");
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteStalledEvent>().Should().BeEmpty();
        harness.Alerts.Published.Should().BeEmpty();

        // Recovery needed nothing but the durable retries: one source event was observed, the
        // materializer was ensured exactly once and the same actor instance stayed active.
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusTerminalStartedEvent>()
            .Should().ContainSingle();
        harness.Agent.State.ActivationGeneration.Should().Be(1);
        harness.Streams.GetStream(SourceScopeActorId).UpsertCount.Should().Be(1, "no ensure command was needed");
        harness.Outbox.Published.Should().BeEmpty();
        harness.EventSourcing.PersistedEvents.Select(static evt => evt.GetType()).Should().Equal(
            typeof(ProjectionScopeStatusTerminalStartedEvent),
            typeof(ProjectionScopeStatusWriteDeferredEvent),
            typeof(ProjectionScopeStatusWriteDeferredEvent),
            typeof(ProjectionScopeStatusWriteDeferredEvent),
            typeof(ProjectionScopeStatusWriteDeferredEvent),
            typeof(ProjectionScopeStatusWriteRecoveredEvent));
    }

    [Fact]
    public async Task HandleRetryWriteAsync_WhenFiveTransientFailures_ShouldMarkStalledOnceAndKeepRetryingAtCappedDelay()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        // attempts 2..4: still below the stalled threshold
        await harness.FireLatestRetryAsync();
        await harness.FireLatestRetryAsync();
        await harness.FireLatestRetryAsync();
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold - 1);
        harness.Agent.State.PendingWrite.Stalled.Should().BeFalse();
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteStalledEvent>().Should().BeEmpty();
        harness.Alerts.Published.Should().BeEmpty();

        // attempt 5: crosses the threshold exactly once
        await harness.FireLatestRetryAsync();

        harness.Agent.State.PendingWrite!.Attempts.Should().Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold);
        harness.Agent.State.PendingWrite.Stalled.Should().BeTrue();
        harness.Agent.State.PendingWrite.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Transient);
        var stalled = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteStalledEvent>()
            .Should().ContainSingle().Subject;
        stalled.Attempts.Should().Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold);
        stalled.Source.Should().Be(harness.Agent.State.PendingWrite.Source);
        stalled.OccurredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(FixedNow));
        var stalledDeferral = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>()
            .Single(static evt => evt.Pending.Attempts == ProjectionScopeStatusGAgent.StalledAttemptThreshold);
        stalledDeferral.Pending.Stalled.Should().BeTrue("the deferred event itself carries the stalled flag");
        harness.EventSourcing.PersistedEvents.IndexOf(stalled).Should().Be(
            harness.EventSourcing.PersistedEvents.IndexOf(stalledDeferral) + 1,
            "the stalled fact is persisted right after the deferral that crossed the threshold");
        var alert = harness.Alerts.Published.Should().ContainSingle().Subject;
        alert.Stage.Should().Be(TerminalWriteAlertStage);
        alert.Kind.Should().Be(ProjectionFailureAlertKind.FailureRecorded);
        alert.ScopeKey.Should().Be(new ProjectionRuntimeScopeKey(
            SourceScopeActorId,
            ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            ProjectionRuntimeMode.DurableMaterialization));
        alert.FailureId.Should().Be($"{SourceScopeActorId}:12:evt-12");
        alert.EventId.Should().Be("evt-12");
        alert.SourceVersion.Should().Be(12);
        alert.Reason.Should().Be(nameof(IOException));
        alert.OccurredAt.Should().Be(FixedNow);
        harness.Callbacks.Timeouts.Should().HaveCount(5);
        harness.Callbacks.Timeouts[^1].DueTime.Should().Be(CappedRetryDelay);
        UnpackRetryCommand(harness.Callbacks.Timeouts[^1]).Attempt.Should().Be(5);

        // attempts 6 and 7: retries continue at the capped cadence, no second stalled fact/alert
        await harness.FireLatestRetryAsync();
        await harness.FireLatestRetryAsync();

        harness.Agent.State.PendingWrite!.Attempts.Should().Be(7);
        harness.Agent.State.PendingWrite.Stalled.Should().BeTrue();
        harness.Agent.State.PendingWrite.NextRetryAtUtc.Should().Be(Timestamp.FromDateTimeOffset(FixedNow + CappedRetryDelay));
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteStalledEvent>().Should().ContainSingle();
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>()
            .Where(static evt => evt.Pending.Attempts > ProjectionScopeStatusGAgent.StalledAttemptThreshold)
            .Should().HaveCount(2)
            .And.OnlyContain(static evt => evt.Pending.Stalled);
        harness.Alerts.Published.Should().ContainSingle();
        harness.Callbacks.Timeouts.Should().HaveCount(7);
        harness.Callbacks.Timeouts.Skip(4).Select(static timeout => timeout.DueTime)
            .Should().Equal(CappedRetryDelay, CappedRetryDelay, CappedRetryDelay);
        harness.Callbacks.Timeouts.Select(timeout => UnpackRetryCommand(timeout).Attempt).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        harness.Dispatcher.Documents.Should().BeEmpty();

        // recovery clears the pending write, and with it the stalled flag
        harness.Dispatcher.Recover();
        await harness.FireLatestRetryAsync();

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(12);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>().Should().ContainSingle();
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().HaveCount(7);
        harness.Alerts.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleRetryWriteAsync_WithStaleExpectedSource_ShouldBeIgnored()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;

        await harness.Agent.HandleRetryWriteAsync(new RetryProjectionScopeStatusWriteCommand
        {
            ExpectedSource = new ProjectionSourceCoordinate
            {
                ActorId = SourceScopeActorId,
                StateVersion = 11,
                EventId = "evt-11",
            },
            Attempt = 1,
        });

        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore);
        harness.Agent.State.PendingWrite.Should().NotBeNull();
        harness.Callbacks.Timeouts.Should().ContainSingle("a stale retry schedules nothing");
    }

    [Fact]
    public async Task HandleRetryWriteAsync_WithSameSourceButSupersededAttempt_ShouldDoNothingUntilTheCurrentAttemptFires()
    {
        // A delayed callback of an earlier attempt for the SAME source: the durable retry state
        // has already advanced past it. It must not write, must not re-defer at a shorter backoff
        // and must not arm a second live retry — the attempt fence is the whole guard.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        await harness.FireLatestRetryAsync();
        var pending = harness.Agent.State.PendingWrite!;
        pending.Attempts.Should().Be(2);
        var nextRetryBefore = pending.NextRetryAtUtc;
        nextRetryBefore.Should().Be(Timestamp.FromDateTimeOffset(FixedNow + ProjectionScopeStatusGAgent.WriteRetryDelays[1]));
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;
        var timeoutsBefore = harness.Callbacks.Timeouts.Count;
        var upsertsBefore = harness.Dispatcher.UpsertAttempts;

        await harness.Agent.HandleRetryWriteAsync(new RetryProjectionScopeStatusWriteCommand
        {
            ExpectedSource = pending.Source.Clone(),
            Attempt = 1,
        });

        harness.Dispatcher.UpsertAttempts.Should().Be(upsertsBefore, "a superseded attempt never writes");
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore, "and persists nothing");
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(2, "the retry state stays where it advanced to");
        harness.Agent.State.PendingWrite.NextRetryAtUtc.Should().Be(nextRetryBefore);
        harness.Agent.State.PendingWrite.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Transient);
        harness.Agent.State.PendingWrite.Stalled.Should().BeFalse();
        harness.Callbacks.Timeouts.Should().HaveCount(timeoutsBefore, "one live retry, armed by the current attempt");

        // the current attempt fires and does the work
        harness.Dispatcher.Recover();
        await harness.Agent.HandleRetryWriteAsync(new RetryProjectionScopeStatusWriteCommand
        {
            ExpectedSource = pending.Source.Clone(),
            Attempt = 2,
        });

        harness.Dispatcher.UpsertAttempts.Should().Be(upsertsBefore + 1);
        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(12);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>()
            .Should().ContainSingle().Which.Source.Should().Be(pending.Source);
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().HaveCount(timeoutsBefore, "no further retry after recovery");
    }

    [Fact]
    public async Task HandleRetryWriteAsync_WithoutPendingWrite_ShouldBeIgnored()
    {
        var harness = await TerminalHarness.CreateStartedAsync();

        await harness.Agent.HandleRetryWriteAsync(new RetryProjectionScopeStatusWriteCommand
        {
            ExpectedSource = new ProjectionSourceCoordinate { ActorId = SourceScopeActorId, StateVersion = 1 },
            Attempt = 1,
        });

        harness.Dispatcher.UpsertAttempts.Should().Be(0);
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
        harness.Callbacks.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_LaterSuccessfulHigherVersion_ShouldRecoverOlderPending()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Agent.State.PendingWrite.Should().NotBeNull();

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 13, eventId: "evt-13", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(13);
        var recovered = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>()
            .Should().ContainSingle().Subject;
        recovered.Source.StateVersion.Should().Be(13);
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().ContainSingle("the superseded retry is not re-armed");
    }

    [Fact]
    public async Task HandleRetryWriteAsync_AfterHigherVersionRecoveredPending_ShouldBeIgnored()
    {
        // The durable retry armed for the superseded pending write may still fire later; it must
        // not write the stale document again.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        var staleRetry = UnpackRetryCommand(harness.Callbacks.Timeouts.Single());
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 13, eventId: "evt-13", new ProjectionScopeWatermarkAdvancedEvent())));
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;

        await harness.Agent.HandleRetryWriteAsync(staleRetry);

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(13);
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore);
        harness.Callbacks.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_LaterSuccessfulLowerVersion_ShouldKeepNewerPending()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 11, eventId: "evt-11", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(11);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>().Should().BeEmpty();
        harness.Agent.State.PendingWrite!.Source.StateVersion.Should().Be(12);
        harness.Callbacks.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_FailedLowerVersionWhilePendingHigher_ShouldNotSupersedePending()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;
        var timeoutsBefore = harness.Callbacks.Timeouts.Count;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 11, eventId: "evt-11", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore, "the newer pending write wins");
        harness.Callbacks.Timeouts.Should().HaveCount(timeoutsBefore, "the newer pending write keeps its own retry");
        harness.Agent.State.PendingWrite!.Source.StateVersion.Should().Be(12);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_FailedHigherVersionWhilePending_ShouldSupersedePendingAndRestartAttempts()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        await harness.FireLatestRetryAsync();
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(2);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 13, eventId: "evt-13", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Agent.State.PendingWrite!.Source.StateVersion.Should().Be(13);
        harness.Agent.State.PendingWrite.Attempts.Should().Be(1, "a new source outcome starts its own retry cadence");
        var latest = harness.Callbacks.Timeouts[^1];
        latest.DueTime.Should().Be(ProjectionScopeStatusGAgent.WriteRetryDelays[0]);
        UnpackRetryCommand(latest).ExpectedSource.StateVersion.Should().Be(13);
        UnpackRetryCommand(latest).Attempt.Should().Be(1);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenWriteThrowsWithoutCallbackScheduler_ShouldDeferWithoutScheduling()
    {
        // No durable scheduler registered: the deferral is still the durable fact (warning path),
        // nothing is scheduled and nothing is published; a later source outcome still recovers it.
        var harness = await TerminalHarness.CreateStartedAsync(withCallbackScheduler: false);
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        await act.Should().NotThrowAsync();
        var deferred = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>()
            .Should().ContainSingle().Subject;
        deferred.Pending.Attempts.Should().Be(1);
        deferred.Pending.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Transient);
        deferred.Pending.NextRetryAtUtc.Should().Be(
            Timestamp.FromDateTimeOffset(FixedNow + ProjectionScopeStatusGAgent.WriteRetryDelays[0]));
        harness.Agent.State.PendingWrite.Should().Be(deferred.Pending);
        harness.Agent.State.Active.Should().BeTrue();
        harness.Agent.State.Released.Should().BeFalse();
        harness.Outbox.Published.Should().BeEmpty();
        harness.Alerts.Published.Should().BeEmpty();

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 13, eventId: "evt-13", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(13);
        harness.Agent.State.PendingWrite.Should().BeNull();
    }

    [Fact]
    public async Task ActivateAsync_WithoutCallbackScheduler_AndTransientPending_ShouldActivateWithoutScheduling()
    {
        var harness = await TerminalHarness.CreateStartedAsync(withCallbackScheduler: false);
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        var reactivated = harness.CreateReplacementActor();
        var act = () => reactivated.ActivateAsync();

        await act.Should().NotThrowAsync();
        reactivated.State.PendingWrite.Should().NotBeNull();
        reactivated.State.PendingWrite!.Attempts.Should().Be(1);
        harness.Outbox.Published.Should().BeEmpty();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().NotBeNull();
    }

    [Fact]
    public async Task ActivateAsync_WithTransientPendingWrite_ShouldReArmDurableRetryAtPendingAttemptAndReassertRelay()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        await harness.FireLatestRetryAsync();
        await harness.FireLatestRetryAsync();
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(3);
        harness.Callbacks.Timeouts.Clear();
        harness.Streams.GetStream(SourceScopeActorId).Relays.Clear();

        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        reactivated.State.PendingWrite.Should().NotBeNull();
        reactivated.State.PendingWrite!.Source.StateVersion.Should().Be(12);
        reactivated.State.PendingWrite.Attempts.Should().Be(3);
        var timeout = harness.Callbacks.Timeouts.Should().ContainSingle().Subject;
        timeout.ActorId.Should().Be(TerminalActorId);
        timeout.CallbackId.Should().Be(ProjectionScopeStatusGAgent.WriteRetryCallbackId);
        timeout.DueTime.Should().Be(ProjectionScopeStatusGAgent.WriteRetryDelays[2],
            "the re-armed retry continues the persisted cadence, it does not restart it");
        var command = UnpackRetryCommand(timeout);
        command.Attempt.Should().Be(3);
        command.ExpectedSource.Should().Be(reactivated.State.PendingWrite.Source);
        harness.Outbox.Published.Should().BeEmpty();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().NotBeNull(
            "activation re-asserts the relay evidence on the source stream");

        // the re-armed retry fires on the replacement actor and completes the write once the store is back
        harness.Dispatcher.Recover();
        await reactivated.HandleRetryWriteAsync(command);

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(12);
        reactivated.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task ActivateAsync_WithStalledTransientPendingWrite_ShouldReArmAtCappedDelay()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        for (var attempt = 1; attempt < ProjectionScopeStatusGAgent.StalledAttemptThreshold + 1; attempt++)
            await harness.FireLatestRetryAsync();
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold + 1);
        harness.Agent.State.PendingWrite.Stalled.Should().BeTrue();
        harness.Callbacks.Timeouts.Clear();

        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        reactivated.State.PendingWrite!.Stalled.Should().BeTrue("the stalled fact is durable");
        var timeout = harness.Callbacks.Timeouts.Should().ContainSingle().Subject;
        timeout.DueTime.Should().Be(CappedRetryDelay);
        UnpackRetryCommand(timeout).Attempt.Should().Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold + 1);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteStalledEvent>()
            .Should().ContainSingle("activation persists nothing");
        harness.Alerts.Published.Should().ContainSingle("activation alerts nothing");
    }

    [Fact]
    public async Task ActivateAsync_WithoutPendingWrite_ShouldNotScheduleRetry()
    {
        var harness = await TerminalHarness.CreateStartedAsync();

        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        reactivated.State.Active.Should().BeTrue();
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Outbox.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivateAsync_WhenReleased_ShouldNotReArmPendingRetry()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());
        harness.Callbacks.Timeouts.Clear();

        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        reactivated.State.Released.Should().BeTrue();
        reactivated.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().BeEmpty();
    }

    // ─── 6b. Pending writes persisted by the previous binary (no failure kind) ──

    [Fact]
    public async Task ActivateAsync_WithPendingWriteWithoutFailureKind_ShouldReArmDurableRetryAtPendingAttempt()
    {
        // The previous binary deferred a write on a store exception without recording a failure
        // kind (Unspecified). It is a transient failure by construction and must be re-armed at
        // its persisted attempt, not ignored.
        var harness = await TerminalHarness.CreateStartedAsync();
        var envelope = BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent()));
        var pending = await harness.SeedPendingWriteFromPreviousBinaryAsync(envelope, attempts: 2);
        pending.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Unspecified);
        harness.Callbacks.Timeouts.Should().BeEmpty();

        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        reactivated.State.PendingWrite.Should().Be(pending);
        var timeout = harness.Callbacks.Timeouts.Should().ContainSingle().Subject;
        timeout.ActorId.Should().Be(TerminalActorId);
        timeout.CallbackId.Should().Be(ProjectionScopeStatusGAgent.WriteRetryCallbackId);
        timeout.DueTime.Should().Be(ProjectionScopeStatusGAgent.WriteRetryDelays[1],
            "the re-armed retry continues the persisted cadence at attempt 2");
        var command = UnpackRetryCommand(timeout);
        command.Attempt.Should().Be(2);
        command.ExpectedSource.Should().Be(pending.Source);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>()
            .Should().ContainSingle("activation persists nothing");
    }

    [Fact]
    public async Task HandleRetryWriteAsync_ForPendingWriteWithoutFailureKind_ShouldRetryAndRecover()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var envelope = BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent()));
        var pending = await harness.SeedPendingWriteFromPreviousBinaryAsync(envelope, attempts: 2);
        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        await reactivated.HandleRetryWriteAsync(UnpackRetryCommand(harness.Callbacks.Timeouts.Single()));

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(12);
        var recovered = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>()
            .Should().ContainSingle().Subject;
        recovered.Source.Should().Be(pending.Source);
        reactivated.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().ContainSingle("no further retry after recovery");
    }

    [Fact]
    public async Task HandleRetryWriteAsync_ForPendingWriteWithoutFailureKind_WhenStoreStillDown_ShouldDeferAsTransientAndContinueCadence()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var envelope = BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent()));
        await harness.SeedPendingWriteFromPreviousBinaryAsync(envelope, attempts: 2);
        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));

        await reactivated.HandleRetryWriteAsync(UnpackRetryCommand(harness.Callbacks.Timeouts.Single()));

        reactivated.State.PendingWrite!.Attempts.Should().Be(3);
        reactivated.State.PendingWrite.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Transient,
            "this binary records the failure kind it observed");
        harness.Callbacks.Timeouts.Should().HaveCount(2);
        harness.Callbacks.Timeouts[^1].DueTime.Should().Be(ProjectionScopeStatusGAgent.WriteRetryDelays[2]);
        UnpackRetryCommand(harness.Callbacks.Timeouts[^1]).Attempt.Should().Be(3);
    }

    [Fact]
    public async Task ActivateAsync_WithRejectedPendingWriteFromDurableLog_ShouldReArmAtTheCappedDelayAndHealOnAnAcceptedRetry()
    {
        // A rejected pending write left in the durable log by an earlier activation is re-armed
        // at the capped cadence: the fact stays observable and one accepted retry clears it.
        var harness = await TerminalHarness.CreateStartedAsync();
        var envelope = BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent()));
        var pending = await harness.SeedPendingWriteFromPreviousBinaryAsync(
            envelope,
            attempts: 1,
            failureKind: ProjectionScopeStatusWriteFailureKind.Rejected);

        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        reactivated.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        var timeout = harness.Callbacks.Timeouts.Should().ContainSingle().Subject;
        timeout.DueTime.Should().Be(CappedRetryDelay, "a rejected pending write is re-armed at the capped cadence");
        UnpackRetryCommand(timeout).Attempt.Should().Be(1);
        UnpackRetryCommand(timeout).ExpectedSource.Should().Be(pending.Source);

        // still rejected: re-deferred at the same cadence, and no second alert for this source
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(ProjectionWriteDisposition.Conflict));
        await reactivated.HandleRetryWriteAsync(UnpackRetryCommand(timeout));

        harness.Dispatcher.UpsertAttempts.Should().Be(1);
        reactivated.State.PendingWrite!.Attempts.Should().Be(2);
        reactivated.State.PendingWrite.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.Callbacks.Timeouts.Should().HaveCount(2);
        harness.Callbacks.Timeouts[^1].DueTime.Should().Be(CappedRetryDelay);
        harness.Alerts.Published.Should().BeEmpty(
            "the pending write was already rejected for this source before the retry ran");

        // the store now accepts the write: the pending fact is cleared
        await reactivated.HandleRetryWriteAsync(UnpackRetryCommand(harness.Callbacks.Timeouts[^1]));

        harness.Dispatcher.UpsertAttempts.Should().Be(2);
        harness.Dispatcher.Writes.Select(static write => write.Disposition).Should().Equal(
            ProjectionWriteDisposition.Conflict,
            ProjectionWriteDisposition.Applied);
        harness.Dispatcher.Documents[^1].StateVersion.Should().Be(12);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>()
            .Should().ContainSingle().Which.Source.Should().Be(pending.Source);
        reactivated.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().HaveCount(2, "no further retry follows the accepted write");
    }

    // ─── 7. Dispositions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProjectionWriteDisposition.Conflict)]
    [InlineData(ProjectionWriteDisposition.Gap)]
    public async Task HandleObservedEnvelopeAsync_RejectedDisposition_ShouldFailTheObservationAlertOnceAndPersistNothing(
        ProjectionWriteDisposition disposition)
    {
        // A rejected write on the OBSERVED path never advances delivery: the observation itself
        // fails, so the provider redelivers it without advancing its checkpoint. Nothing about
        // this observation becomes a durable fact of the materializer — no pending write, no
        // deferral, no retry — because the envelope is still owned by the provider.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(disposition));
        var envelope = BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent()));

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(envelope);

        var rejected = (await act.Should().ThrowExactlyAsync<ProjectionScopeStatusWriteRejectedException>(
            "the observation must fail so the provider redelivers it")).Which;
        rejected.MaterializerActorId.Should().Be(TerminalActorId);
        rejected.Disposition.Should().Be(disposition);
        rejected.Source.Should().Be(new ProjectionSourceCoordinate
        {
            ActorId = SourceScopeActorId,
            StateVersion = 12,
            EventId = "evt-12",
        });

        harness.Dispatcher.UpsertAttempts.Should().Be(1);
        harness.EventSourcing.PersistedEvents.Should().ContainSingle(
                "a redelivered observation leaves no durable trace behind")
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().BeEmpty("the provider owns the redelivery, not a durable retry");
        harness.Outbox.Published.Should().BeEmpty();
        harness.Outbox.Sent.Should().BeEmpty();
        var alert = harness.Alerts.Published.Should().ContainSingle().Subject;
        alert.Kind.Should().Be(ProjectionFailureAlertKind.FailureRecorded);
        alert.Stage.Should().Be(TerminalWriteAlertStage);
        alert.Reason.Should().Be(disposition.ToString());
        alert.ScopeKey.Should().Be(new ProjectionRuntimeScopeKey(
            SourceScopeActorId,
            ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            ProjectionRuntimeMode.DurableMaterialization));
        alert.SourceVersion.Should().Be(12);
        alert.EventId.Should().Be("evt-12");
        alert.FailureId.Should().Be($"{SourceScopeActorId}:12:evt-12");
        alert.EventType.Should().Be(nameof(ProjectionScopeStatusDocument));
        alert.UnresolvedFailureCount.Should().Be(1);
        alert.OccurredAt.Should().Be(FixedNow);

        // the provider redelivers the very same envelope; the store now accepts it
        await harness.Agent.HandleObservedEnvelopeAsync(envelope);

        harness.Dispatcher.Writes.Select(static write => write.Disposition).Should().Equal(
            disposition,
            ProjectionWriteDisposition.Applied);
        harness.Dispatcher.Documents[^1].StateVersion.Should().Be(12);
        harness.Agent.State.PendingWrite.Should().BeNull("the redelivery succeeded, so there is nothing pending");
        harness.EventSourcing.PersistedEvents.Should().ContainSingle(
            "a successful write with no pending write persists nothing either");
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Alerts.Published.Should().ContainSingle("the recovered redelivery raises no new alert");
    }

    [Theory]
    [InlineData(ProjectionWriteDisposition.Conflict)]
    [InlineData(ProjectionWriteDisposition.Gap)]
    public async Task HandleObservedEnvelopeAsync_RejectedDisposition_WithoutAlertSink_ShouldStillFailTheObservation(
        ProjectionWriteDisposition disposition)
    {
        var harness = await TerminalHarness.CreateStartedAsync(withAlertSink: false);
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(disposition));

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        (await act.Should().ThrowExactlyAsync<ProjectionScopeStatusWriteRejectedException>())
            .Which.Disposition.Should().Be(disposition);
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
        harness.Callbacks.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_RejectedDisposition_WhenAlertSinkThrows_ShouldStillFailTheObservation()
    {
        // The alert is best-effort: its failure is swallowed. What the caller sees is the
        // rejection itself, so the provider still redelivers instead of advancing.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Alerts.ThrowOnPublish(new InvalidOperationException("alert channel down"));
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(ProjectionWriteDisposition.Gap));

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        var thrown = (await act.Should().ThrowExactlyAsync<ProjectionScopeStatusWriteRejectedException>(
            "the sink's own failure must never mask the rejection")).Which;
        thrown.Disposition.Should().Be(ProjectionWriteDisposition.Gap);
        thrown.Message.Should().NotContain("alert channel down");
        harness.Alerts.Published.Should().BeEmpty("the sink threw before recording anything");
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Callbacks.Timeouts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProjectionWriteDisposition.Duplicate)]
    [InlineData(ProjectionWriteDisposition.Stale)]
    public async Task HandleObservedEnvelopeAsync_NonTerminalDisposition_ShouldBeTreatedAsSuccess(
        ProjectionWriteDisposition disposition)
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(disposition));

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().ContainSingle();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Alerts.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_DuplicateWhilePending_ShouldRecoverPending()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Dispatcher.Enqueue(ProjectionWriteResult.Duplicate());

        await harness.FireLatestRetryAsync();

        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>().Should().ContainSingle();
        harness.Agent.State.PendingWrite.Should().BeNull();
    }

    [Theory]
    [InlineData(ProjectionWriteDisposition.Conflict)]
    [InlineData(ProjectionWriteDisposition.Gap)]
    public async Task HandleRetryWriteAsync_WhenRetryIsRejected_ShouldStayDurablyRetryableAtTheCappedCadence(
        ProjectionWriteDisposition disposition)
    {
        // On the DURABLE RETRY path the envelope is already the materializer's own fact: a
        // rejection cannot be redelivered by anyone, so the pending write stays visible and
        // durably retryable at the capped cadence instead of being silently dropped. Re-arming
        // the same bytes cannot heal it, but a later write at or above its version can.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Agent.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Transient);
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(disposition));

        await harness.FireLatestRetryAsync();

        var pending = harness.Agent.State.PendingWrite!;
        pending.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        pending.Attempts.Should().Be(2);
        pending.LastError.Should().Be(disposition.ToString());
        pending.Stalled.Should().BeFalse("stalling counts transient outages, not rejections");
        pending.NextRetryAtUtc.Should().Be(Timestamp.FromDateTimeOffset(FixedNow + CappedRetryDelay),
            "a rejected pending write is retried at the capped cadence, never dropped");
        harness.Callbacks.Timeouts.Should().HaveCount(2);
        harness.Callbacks.Timeouts[^1].DueTime.Should().Be(CappedRetryDelay);
        var rearmed = UnpackRetryCommand(harness.Callbacks.Timeouts[^1]);
        rearmed.Attempt.Should().Be(2, "the re-armed retry carries the pending write's own attempt");
        rearmed.ExpectedSource.Should().Be(pending.Source);
        harness.Alerts.Published.Should().ContainSingle().Which.Stage.Should().Be(TerminalWriteAlertStage);
        harness.Alerts.Published[0].Reason.Should().Be(disposition.ToString());

        // a further retry that still conflicts re-defers at the same cadence, without re-alerting
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(disposition));
        await harness.FireLatestRetryAsync();

        harness.Agent.State.PendingWrite!.Attempts.Should().Be(3);
        harness.Agent.State.PendingWrite.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.Agent.State.PendingWrite.NextRetryAtUtc.Should().Be(
            Timestamp.FromDateTimeOffset(FixedNow + CappedRetryDelay));
        harness.Callbacks.Timeouts.Should().HaveCount(3);
        harness.Callbacks.Timeouts[^1].DueTime.Should().Be(CappedRetryDelay);
        UnpackRetryCommand(harness.Callbacks.Timeouts[^1]).Attempt.Should().Be(3);
        harness.Alerts.Published.Should().ContainSingle("an already-rejected source is alerted once, not once per retry");

        // a later terminal outcome at a higher version writes through and clears the rejection
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 13, eventId: "evt-13", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents[^1].StateVersion.Should().Be(13);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>()
            .Should().ContainSingle().Which.Source.StateVersion.Should().Be(13);
        harness.Agent.State.PendingWrite.Should().BeNull(
            "a later higher-version terminal write supersedes the rejection");
        harness.Callbacks.Timeouts.Should().HaveCount(3, "the superseded retry is not re-armed");
        harness.Alerts.Published.Should().ContainSingle();
    }

    [Theory]
    [InlineData(ProjectionWriteDisposition.Conflict)]
    [InlineData(ProjectionWriteDisposition.Gap)]
    public async Task HandleRetryWriteAsync_WhenRejectedPastTheStalledThreshold_ShouldNeverBecomeStalled(
        ProjectionWriteDisposition disposition)
    {
        // Stalling counts transient store outages, not rejections: a rejected pending write is
        // retried at the capped cadence for as long as it takes, and crossing the attempt
        // threshold must NOT mark it stalled, raise a stalled fact or alert a second time — the
        // store is up and answering, so there is no outage to escalate.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(1);

        // attempts 2..6: every retry is rejected, so the pending write passes the threshold
        for (var rejection = 0; rejection < ProjectionScopeStatusGAgent.StalledAttemptThreshold; rejection++)
        {
            harness.Dispatcher.Enqueue(new ProjectionWriteResult(disposition));
            await harness.FireLatestRetryAsync();
        }

        var pending = harness.Agent.State.PendingWrite!;
        pending.Attempts.Should().Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold + 1,
            "the rejected write stayed durably retryable past the threshold");
        pending.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        pending.Stalled.Should().BeFalse("a rejection is not an outage: it never counts toward stalling");
        pending.NextRetryAtUtc.Should().Be(Timestamp.FromDateTimeOffset(FixedNow + CappedRetryDelay));
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteStalledEvent>().Should().BeEmpty(
            "no stalled fact is ever raised for a rejected write");
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>()
            .Where(static evt => evt.Pending.Attempts >= ProjectionScopeStatusGAgent.StalledAttemptThreshold)
            .Should().HaveCount(2, "attempts 5 and 6 are at or past the threshold")
            .And.OnlyContain(static evt =>
                !evt.Pending.Stalled &&
                evt.Pending.FailureKind == ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.Alerts.Published.Should().ContainSingle(
            "an already-rejected source is alerted once, not again at the threshold")
            .Which.Reason.Should().Be(disposition.ToString());
        harness.Callbacks.Timeouts.Should().HaveCount(ProjectionScopeStatusGAgent.StalledAttemptThreshold + 1);
        harness.Callbacks.Timeouts.Skip(1).Should().OnlyContain(timeout => timeout.DueTime == CappedRetryDelay);
        harness.Dispatcher.Writes.Should()
            .HaveCount(ProjectionScopeStatusGAgent.StalledAttemptThreshold, "the first attempt threw, the rest were rejected")
            .And.OnlyContain(write => write.Disposition == disposition, "not one retry was ever applied");
    }

    [Fact]
    public async Task HandleRetryWriteAsync_WhenAStalledPendingWriteIsThenRejected_ShouldKeepItsStalledFact()
    {
        // The outage stalled the pending write; the store then comes back but rejects the bytes.
        // The failure kind moves to Rejected (capped cadence, one fresh alert) while the stalled
        // fact it already earned is carried over — an operator must not see it silently un-stall.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        for (var attempt = 1; attempt < ProjectionScopeStatusGAgent.StalledAttemptThreshold; attempt++)
            await harness.FireLatestRetryAsync();
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold);
        harness.Agent.State.PendingWrite.Stalled.Should().BeTrue();
        harness.Alerts.Published.Should().ContainSingle("the outage alerted when it stalled");

        harness.Dispatcher.Recover();
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(ProjectionWriteDisposition.Conflict));
        await harness.FireLatestRetryAsync();

        var pending = harness.Agent.State.PendingWrite!;
        pending.Attempts.Should().Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold + 1);
        pending.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        pending.Stalled.Should().BeTrue("a pending write that already stalled stays stalled once it is also rejected");
        pending.LastError.Should().Be(nameof(ProjectionWriteDisposition.Conflict));
        pending.NextRetryAtUtc.Should().Be(Timestamp.FromDateTimeOffset(FixedNow + CappedRetryDelay));
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>().Last()
            .Pending.Stalled.Should().BeTrue("the deferral that recorded the rejection carries the stalled fact forward");
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteStalledEvent>().Should().ContainSingle(
            "the stalled fact is raised once, never again by the rejection");
        harness.Alerts.Published.Should().HaveCount(2, "the first rejection of this source alerts on its own");
        harness.Alerts.Published[^1].Reason.Should().Be(nameof(ProjectionWriteDisposition.Conflict));
        harness.Callbacks.Timeouts[^1].DueTime.Should().Be(CappedRetryDelay);
        UnpackRetryCommand(harness.Callbacks.Timeouts[^1]).Attempt.Should()
            .Be(ProjectionScopeStatusGAgent.StalledAttemptThreshold + 1);
    }

    [Fact]
    public async Task ActivateAsync_WithRejectedPendingWrite_ShouldReArmAtTheCappedDelay()
    {
        // The rejected pending write survives deactivation and is re-armed on activation: it
        // stays observable and retryable until a later write clears it.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(ProjectionWriteDisposition.Conflict));
        await harness.FireLatestRetryAsync();
        harness.Agent.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.Callbacks.Timeouts.Clear();
        harness.Alerts.Published.Clear();
        // The relay is dropped first so the assertion below proves activation WRITES it again,
        // instead of merely finding the one this materializer already owned.
        harness.Streams.GetStream(SourceScopeActorId).Relays.Clear();
        var upsertsBefore = harness.Streams.GetStream(SourceScopeActorId).UpsertCount;
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().BeNull();

        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        reactivated.State.PendingWrite.Should().NotBeNull();
        reactivated.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        reactivated.State.PendingWrite.Attempts.Should().Be(2);
        var timeout = harness.Callbacks.Timeouts.Should().ContainSingle(
            "every pending write stays durably retryable across activations").Subject;
        timeout.ActorId.Should().Be(TerminalActorId);
        timeout.CallbackId.Should().Be(ProjectionScopeStatusGAgent.WriteRetryCallbackId);
        timeout.DueTime.Should().Be(CappedRetryDelay);
        var command = UnpackRetryCommand(timeout);
        command.Attempt.Should().Be(2);
        command.ExpectedSource.Should().Be(reactivated.State.PendingWrite.Source);
        harness.Alerts.Published.Should().BeEmpty("activation does not re-alert");
        harness.Outbox.Published.Should().BeEmpty();
        harness.Streams.GetStream(SourceScopeActorId).UpsertCount.Should().Be(upsertsBefore + 1,
            "activation writes the relay evidence again");
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId),
                SourceScopeActorId,
                TerminalActorId,
                ProjectionScopeStatusGAgent.AgentKind)
            .Should().BeTrue("a rejected pending write keeps the materializer the owner of the source's status");
    }

    // ─── 7b. Document route fence, end to end through the in-memory store ─────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TerminalWrite_SameVersionUnderHigherRouteEpoch_ShouldTakeOverThenDuplicateThenFenceLowerEpoch(long storedEpoch)
    {
        // The document already holds source version V as written by the previous writer: with no
        // route at all (epoch 0, a binary that did not carry the route) or under the phase-less
        // v1 route of epoch 1. The terminal's write of the same version under epoch 2 is the
        // epoch-fenced same-version takeover, not a conflict.
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static document => document.Id);
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.BackBy(store);
        var stateEvent = BuildStateEvent(version: 50, eventId: "evt-50", new ProjectionScopeWatermarkAdvancedEvent());
        var storedState = BuildSourceState();
        storedState.StatusRoute = storedEpoch == 0
            ? null
            : new ProjectionScopeStatusRoute
            {
                ContractId = ProjectionScopeStatusGAgent.ContractId,
                ContractVersion = 1,
                RouteEpoch = storedEpoch,
            };
        var storedDocument = ProjectionScopeStatusDocumentMapper.Map(
            storedState,
            stateEvent,
            stateEvent.Timestamp.ToDateTimeOffset());
        ((IProjectionRouteFencedReadModel)storedDocument).RouteEpoch.Should().Be(storedEpoch);
        (await store.UpsertAsync(storedDocument)).Disposition.Should().Be(ProjectionWriteDisposition.Applied);

        var takeoverState = BuildSourceState();
        takeoverState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2);
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(takeoverState, stateEvent));

        harness.Dispatcher.Writes.Should().Equal((50L, 2L, ProjectionWriteDisposition.Applied));
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
        var current = await store.GetAsync(SourceScopeActorId);
        current.Should().NotBeNull();
        current!.StatusRoute.RouteEpoch.Should().Be(2);
        current.ToByteArray().Should().Equal(
            ProjectionScopeStatusDocumentMapper.Map(takeoverState, stateEvent, stateEvent.Timestamp.ToDateTimeOffset()).ToByteArray());

        // the same write again is an exact duplicate
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(takeoverState, stateEvent));

        harness.Dispatcher.Writes[^1].Should().Be((50L, 2L, ProjectionWriteDisposition.Duplicate));
        harness.Agent.State.PendingWrite.Should().BeNull();

        // a straggler of the same version under a lower epoch is stale: success for the
        // materializer (nothing to defer, nothing to alert), the store keeps the epoch-2 document
        var staleState = BuildSourceState();
        staleState.StatusRoute = new ProjectionScopeStatusRoute
        {
            ContractId = ProjectionScopeStatusGAgent.ContractId,
            ContractVersion = 1,
            RouteEpoch = 1,
        };
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(staleState, stateEvent));

        harness.Dispatcher.Writes[^1].Should().Be((50L, 1L, ProjectionWriteDisposition.Stale));
        harness.Agent.State.PendingWrite.Should().BeNull("Stale is a non-terminal disposition: success");
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>().Should().BeEmpty();
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Alerts.Published.Should().BeEmpty();
        (await store.GetAsync(SourceScopeActorId))!.StatusRoute.RouteEpoch.Should().Be(2);

        // equal epoch, same version, different committed event id: a real conflict. The
        // observation fails so the provider redelivers it; the materializer persists nothing.
        var conflictingEvent = BuildStateEvent(version: 50, eventId: "evt-50-other", new ProjectionScopeWatermarkAdvancedEvent());
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(
            BuildForwardedEnvelope(takeoverState, conflictingEvent));

        var rejected = (await act.Should().ThrowExactlyAsync<ProjectionScopeStatusWriteRejectedException>()).Which;
        rejected.Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
        rejected.Source.EventId.Should().Be("evt-50-other");
        rejected.Source.StateVersion.Should().Be(50);
        harness.Dispatcher.Writes[^1].Should().Be((50L, 2L, ProjectionWriteDisposition.Conflict));
        harness.Agent.State.PendingWrite.Should().BeNull("a redelivered observation leaves nothing pending");
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore);
        harness.Callbacks.Timeouts.Should().BeEmpty("the provider redelivers; no durable retry is armed");
        harness.Alerts.Published.Should().ContainSingle().Which.Reason.Should().Be(nameof(ProjectionWriteDisposition.Conflict));
        (await store.GetAsync(SourceScopeActorId))!.LastEventId.Should().Be("evt-50");
    }

    [Fact]
    public async Task TerminalWrite_SameVersionUnderLowerRouteEpoch_WhenStoreHoldsHigherEpoch_ShouldBeStaleNotPending()
    {
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static document => document.Id);
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.BackBy(store);
        var stateEvent = BuildStateEvent(version: 60, eventId: "evt-60", new ProjectionScopeWatermarkAdvancedEvent());
        var newerState = BuildSourceState();
        newerState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(3);
        (await store.UpsertAsync(ProjectionScopeStatusDocumentMapper.Map(newerState, stateEvent, stateEvent.Timestamp.ToDateTimeOffset())))
            .Disposition.Should().Be(ProjectionWriteDisposition.Applied);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(BuildRoutedSourceState(), stateEvent));

        harness.Dispatcher.Writes.Should().Equal((60L, 1L, ProjectionWriteDisposition.Stale));
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
        harness.Alerts.Published.Should().BeEmpty();
        (await store.GetAsync(SourceScopeActorId))!.StatusRoute.RouteEpoch.Should().Be(3);
    }

    [Fact]
    public async Task TerminalWrite_HigherSourceVersion_ShouldTakeDocumentForwardRegardlessOfStoredEpoch()
    {
        // The fence only applies within one source version: a higher source version always wins.
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static document => document.Id);
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.BackBy(store);
        var storedState = BuildSourceState();
        storedState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(5);
        var storedEvent = BuildStateEvent(version: 70, eventId: "evt-70", new ProjectionScopeWatermarkAdvancedEvent());
        (await store.UpsertAsync(ProjectionScopeStatusDocumentMapper.Map(storedState, storedEvent, storedEvent.Timestamp.ToDateTimeOffset())))
            .Disposition.Should().Be(ProjectionWriteDisposition.Applied);

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 71, eventId: "evt-71", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Writes.Should().Equal((71L, 1L, ProjectionWriteDisposition.Applied));
        (await store.GetAsync(SourceScopeActorId))!.StateVersion.Should().Be(71);
    }

    [Fact]
    public async Task TerminalWrite_AfterInPlaceContractUpgrade_ShouldTakeItsOwnDocumentOverAtTheUpgradedEpoch()
    {
        // The in-place V1 -> V2 contract upgrade: the writer is unchanged (no cutover, nothing to
        // release), the source just commits ProjectionScopeStatusRouteContractUpgradedEvent with
        // this materializer's route moved to the current contract at epoch+1. That commit is
        // itself a terminal outcome, so the materializer's first write after the upgrade is the
        // epoch-fenced takeover of the document it wrote itself under the previous contract.
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static document => document.Id);
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.BackBy(store);
        var beforeUpgrade = BuildSourceState();
        beforeUpgrade.StatusRoute = BuildPreviousContractTerminalRoute(1);
        beforeUpgrade.StatusRoute.LegacyRouteReleased = true;
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            beforeUpgrade,
            BuildStateEvent(version: 90, eventId: "evt-90", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Dispatcher.Writes.Should().Equal((90L, 1L, ProjectionWriteDisposition.Applied));
        (await store.GetAsync(SourceScopeActorId))!.StatusRoute.ContractId.Should().Be(PreviousTerminalContractId);

        // the source upgrades the contract in place; the route it publishes is the one its own
        // applier commits for that event (same writer, next epoch, phase forced Active)
        var upgraded = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            beforeUpgrade.StatusRoute.RouteEpoch + 1,
            ProjectionScopeStatusRoutePhase.Active);
        upgraded.LegacyRouteReleased = beforeUpgrade.StatusRoute.LegacyRouteReleased;
        upgraded.FlipVersion = 91;
        var afterUpgrade = ProjectionScopeStateApplier.ApplyStatusRouteContractUpgraded(
            beforeUpgrade,
            new ProjectionScopeStatusRouteContractUpgradedEvent
            {
                Route = upgraded,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(FixedNow),
            });
        afterUpgrade.Should().NotBeSameAs(beforeUpgrade, "the upgrade applies over a terminal route in a writing phase");
        afterUpgrade.StatusRoute.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        afterUpgrade.StatusRoute.RouteEpoch.Should().Be(2);
        afterUpgrade.StatusRoute.LegacyRouteReleased.Should().BeTrue("no cutover: the legacy release carries over");

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            afterUpgrade,
            BuildStateEvent(version: 91, eventId: "evt-91", new ProjectionScopeStatusRouteContractUpgradedEvent())));

        harness.Dispatcher.Writes[^1].Should().Be((91L, 2L, ProjectionWriteDisposition.Applied),
            "the contract-upgrade commit is a terminal outcome and its write carries the upgraded epoch");
        var current = await store.GetAsync(SourceScopeActorId);
        current!.StatusRoute.Should().Be(afterUpgrade.StatusRoute);
        current.StatusRoute.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        ((IProjectionRouteFencedReadModel)current).RouteEpoch.Should().Be(2);
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle(
                "an in-place upgrade is a fact of the SOURCE; the materializer neither restarts nor releases")
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.Released.Should().BeFalse();
        harness.Outbox.Sent.Should().BeEmpty("the writer is unchanged: nothing is reported and nothing is confirmed");
        harness.Alerts.Published.Should().BeEmpty();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().NotBeNull();

        // a delayed publication that still carries the PRE-upgrade route at the upgraded
        // version is fenced by the epoch: it can never roll the document back to the old contract
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            beforeUpgrade,
            BuildStateEvent(version: 91, eventId: "evt-91", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Writes[^1].Should().Be((91L, 1L, ProjectionWriteDisposition.Stale),
            "the previous contract's route is still served, but its lower epoch loses the same-version fence");
        (await store.GetAsync(SourceScopeActorId))!.StatusRoute.Should().Be(afterUpgrade.StatusRoute);
        harness.Agent.State.PendingWrite.Should().BeNull("Stale is a non-terminal disposition: success");
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
        harness.Alerts.Published.Should().BeEmpty();
        harness.Callbacks.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalWrite_AfterUngatedLegacyShadowWroteSameVersion_ShouldBeExactDuplicate()
    {
        // Rolling window: an old-binary legacy shadow that predates the route gate writes every
        // version through the shared mapper (the document carries the source's route either way).
        // The terminal's write of the same version under the same route is byte-identical: an
        // exact duplicate, never a conflict, nothing pending.
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static document => document.Id);
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.BackBy(store);
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2);
        sourceState.LastSuccessfulVersion = 79;
        var stateEvent = BuildStateEvent(version: 80, eventId: "evt-80", new ProjectionScopeWatermarkAdvancedEvent());
        var envelope = BuildForwardedEnvelope(sourceState, stateEvent);
        var legacyWrite = ProjectionScopeStatusDocumentMapper.Map(
            sourceState,
            stateEvent,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, FixedNow));
        (await store.UpsertAsync(legacyWrite)).Disposition.Should().Be(ProjectionWriteDisposition.Applied);

        await harness.Agent.HandleObservedEnvelopeAsync(envelope);

        harness.Dispatcher.Writes.Should().Equal((80L, 2L, ProjectionWriteDisposition.Duplicate));
        harness.Dispatcher.Documents.Single().ToByteArray().Should().Equal(legacyWrite.ToByteArray());
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Alerts.Published.Should().BeEmpty();
    }

    // ─── 8. Release ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleObservedEnvelopeAsync_SourceReleasedAndDetached_ShouldWriteThenSelfRelease()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildRoutedSourceState();
        sourceState.Released = true;
        sourceState.ObservationAttached = false;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 20, eventId: "evt-20", new ProjectionScopeReleasedEvent())));

        var document = harness.Dispatcher.Documents.Should().ContainSingle().Subject;
        document.Released.Should().BeTrue();
        document.StateVersion.Should().Be(20);
        harness.EventSourcing.PersistedEvents.Should().HaveCount(2);
        harness.EventSourcing.PersistedEvents[1].Should().BeOfType<ProjectionScopeStatusTerminalReleasedEvent>();
        harness.Agent.State.Released.Should().BeTrue();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().BeNull(
            "the relay evidence is removed from the source stream first");
        harness.Streams.GetStream(SourceScopeActorId).RemovedTargets.Should().Equal(TerminalActorId);
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_SourceReleasedButStillAttached_ShouldNotSelfRelease()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildRoutedSourceState();
        sourceState.Released = true;
        sourceState.ObservationAttached = true;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 20, eventId: "evt-20", new ProjectionScopeReleasedEvent())));

        harness.Dispatcher.Documents.Should().ContainSingle();
        harness.Agent.State.Released.Should().BeFalse();
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleReleaseAsync_ShouldRemoveRelayAndPersistReleased()
    {
        var harness = await TerminalHarness.CreateStartedAsync();

        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());

        harness.EventSourcing.PersistedEvents.Should().HaveCount(2);
        harness.EventSourcing.PersistedEvents[1].Should().BeOfType<ProjectionScopeStatusTerminalReleasedEvent>();
        harness.Agent.State.Released.Should().BeTrue();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().BeNull();
        harness.Outbox.Sent.Should().BeEmpty(
            "a plain lifecycle release carries no route epoch, so there is no cutover to confirm");
    }

    [Fact]
    public async Task HandleReleaseAsync_WithStatusRouteEpoch_BeforeDrainCommit_ShouldDoNothing()
    {
        var harness = await TerminalHarness.CreateStartedAsync();

        await DispatchReleaseAsync(
            harness.Agent,
            BuildReleaseCommand(statusRouteEpoch: 4, requiredObservedVersion: 1));

        harness.EventSourcing.PersistedEvents.Should().ContainSingle()
            .Which.Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>();
        harness.Agent.State.Released.Should().BeFalse();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().NotBeNull();
        harness.Outbox.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleReleaseAsync_WithStatusRouteEpoch_AfterObservingItsRollback_ShouldReConfirmTheDrainedVersion()
    {
        // The materializer released itself on the rollback publication it drained at version 17,
        // and the source re-dispatches the release command until it holds a confirmation. The
        // re-confirmation must carry that SAME drained version (the durable
        // ReleasedAtObservedVersion), because the source compares it against the route's blocked
        // version to detect a writer that released before it had drained the blocked publication.
        var harness = await TerminalHarness.CreateStartedAsync();
        var rolledBack = BuildSourceState();
        rolledBack.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(3, ProjectionScopeStatusRoutePhase.Active);
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            rolledBack,
            BuildStateEvent(version: 17, eventId: "evt-17", new ProjectionScopeStatusRouteBlockedEvent())));
        harness.Agent.State.ReleasedAtObservedVersion.Should().Be(17);
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;

        await DispatchReleaseAsync(
            harness.Agent,
            BuildReleaseCommand(statusRouteEpoch: 3, requiredObservedVersion: 17));

        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore, "the release was already committed");
        harness.Outbox.ReleaseConfirmations.Should().ContainSingle()
            .Which.ReleaseConfirmation.Should().Be(new ProjectionScopeStatusWriterReleasedEvent
            {
                SourceScopeActorId = SourceScopeActorId,
                WriterActorId = TerminalActorId,
                RouteEpoch = 3,
                LastObservedVersion = 17,
                ReleasedAtUtc = Timestamp.FromDateTimeOffset(FixedNow),
            });
        harness.Dispatcher.UpsertAttempts.Should().Be(0);
    }

    [Fact]
    public async Task HandleReleaseAsync_WithStatusRouteEpoch_ShouldRequireExactPublisherWriterAndDrainWatermark()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var rolledBack = BuildSourceState();
        rolledBack.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(
            3,
            ProjectionScopeStatusRoutePhase.Active);
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            rolledBack,
            BuildStateEvent(version: 17, eventId: "evt-17", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Agent.State.ReleasedAtObservedVersion.Should().Be(17);

        var wrongWriter = BuildReleaseCommand(statusRouteEpoch: 3, requiredObservedVersion: 17);
        wrongWriter.ExpectedWriterActorId = "other-writer";
        await DispatchReleaseAsync(harness.Agent, wrongWriter);
        await DispatchReleaseAsync(
            harness.Agent,
            BuildReleaseCommand(statusRouteEpoch: 3, requiredObservedVersion: 17),
            publisherActorId: "other-source");
        await DispatchReleaseAsync(
            harness.Agent,
            BuildReleaseCommand(statusRouteEpoch: 3, requiredObservedVersion: 18));

        harness.Outbox.Sent.Should().BeEmpty();
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusTerminalReleasedEvent>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task HandleReleaseAsync_WithStatusRouteEpoch_WhenAlreadyReleased_ShouldReConfirmWithoutCommittingAgain()
    {
        // The source re-dispatches the release until it holds the confirmation, so an already
        // released writer must answer again — and commit nothing the second time.
        var harness = await TerminalHarness.CreateStartedAsync();
        var rolledBack = BuildSourceState();
        rolledBack.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(
            4,
            ProjectionScopeStatusRoutePhase.Active);
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            rolledBack,
            BuildStateEvent(version: 17, eventId: "evt-17", new ProjectionScopeWatermarkAdvancedEvent())));
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;

        var release = BuildReleaseCommand(statusRouteEpoch: 4, requiredObservedVersion: 17);
        await DispatchReleaseAsync(harness.Agent, release);
        await DispatchReleaseAsync(harness.Agent, release);

        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusTerminalReleasedEvent>()
            .Should().ContainSingle("releasing is idempotent");
        harness.Outbox.ReleaseConfirmations.Should().HaveCount(2);
        harness.Outbox.ReleaseConfirmations.Should().OnlyContain(sent =>
            sent.TargetActorId == SourceScopeActorId &&
            ((ProjectionScopeStatusWriterReleasedEvent)sent.Message).RouteEpoch == 4 &&
            ((ProjectionScopeStatusWriterReleasedEvent)sent.Message).LastObservedVersion == 17 &&
            ((ProjectionScopeStatusWriterReleasedEvent)sent.Message).WriterActorId == TerminalActorId);
    }

    [Fact]
    public async Task HandleReleaseAsync_WhenAlreadyReleased_ShouldBeIdempotent()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());

        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());

        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusTerminalReleasedEvent>()
            .Should().ContainSingle();
    }

    [Fact]
    public async Task HandleReleaseAsync_WithMismatchedCommand_ShouldThrow()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var command = BuildReleaseCommand();
        command.ProjectionKind = ProjectionKind;

        var act = () => harness.Agent.HandleReleaseAsync(command);

        await act.Should().ThrowAsync<InvalidOperationException>();
        harness.Agent.State.Released.Should().BeFalse();
    }

    [Fact]
    public async Task HandleReleaseAsync_WithPendingWrite_ShouldClearPending()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());

        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Agent.State.Released.Should().BeTrue();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_AfterRelease_RoutedPublicationOfLiveSource_ShouldRestartAndWrite()
    {
        // A released source scope that is ensured again re-asserts our relay on its own turn;
        // the first routed publication of the live source restarts this materializer.
        var harness = await TerminalHarness.CreateStartedAsync();
        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 21, eventId: "evt-21", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().ContainSingle().Which.StateVersion.Should().Be(21);
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore + 1);
        var restarted = harness.EventSourcing.PersistedEvents[^1]
            .Should().BeOfType<ProjectionScopeStatusTerminalStartedEvent>().Subject;
        restarted.SourceScopeActorId.Should().Be(SourceScopeActorId);
        restarted.ActivationGeneration.Should().Be(2);
        harness.Agent.State.Released.Should().BeFalse();
        harness.Agent.State.Active.Should().BeTrue();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_AfterRelease_StragglerOfReleasedSource_ShouldBeIgnored()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;
        var sourceState = BuildRoutedSourceState();
        sourceState.Released = true;
        sourceState.ObservationAttached = false;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 21, eventId: "evt-21", new ProjectionScopeReleasedEvent())));

        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore);
        harness.Agent.State.Released.Should().BeTrue();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_SourceReleasedAndDetached_WhenWriteFails_ShouldDeferReleaseUntilRecovered()
    {
        // The source's final publication is its release; if that write is deferred the
        // materializer must not release with it (release clears the pending write), otherwise
        // the released status document could never be written.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        var sourceState = BuildRoutedSourceState();
        sourceState.Released = true;
        sourceState.ObservationAttached = false;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 20, eventId: "evt-20", new ProjectionScopeReleasedEvent())));

        harness.Agent.State.Released.Should().BeFalse("a pending write keeps the materializer alive");
        harness.Agent.State.PendingWrite.Should().NotBeNull();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().NotBeNull(
            "the relay stays until the deferred final write lands");
        var timeout = harness.Callbacks.Timeouts.Should().ContainSingle().Subject;
        timeout.CallbackId.Should().Be(ProjectionScopeStatusGAgent.WriteRetryCallbackId);
        timeout.DueTime.Should().Be(ProjectionScopeStatusGAgent.WriteRetryDelays[0]);

        await harness.Agent.HandleRetryWriteAsync(UnpackRetryCommand(timeout));

        var document = harness.Dispatcher.Documents.Should().ContainSingle().Subject;
        document.Released.Should().BeTrue();
        document.StateVersion.Should().Be(20);
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Agent.State.Released.Should().BeTrue("the recovered write completes the release with the source");
        harness.EventSourcing.PersistedEvents.Select(static evt => evt.GetType()).Should().Equal(
            typeof(ProjectionScopeStatusTerminalStartedEvent),
            typeof(ProjectionScopeStatusWriteDeferredEvent),
            typeof(ProjectionScopeStatusWriteRecoveredEvent),
            typeof(ProjectionScopeStatusTerminalReleasedEvent));
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().BeNull();
        harness.Callbacks.Timeouts.Should().ContainSingle("no further retry is scheduled after the release");
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_SourceReleasedAndDetached_WhenWriteKeepsFailing_ShouldKeepRetryingAndReleaseOnRecovery()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.AlwaysThrow(new IOException("store unavailable"));
        var sourceState = BuildRoutedSourceState();
        sourceState.Released = true;
        sourceState.ObservationAttached = false;
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 20, eventId: "evt-20", new ProjectionScopeReleasedEvent())));

        await harness.FireLatestRetryAsync();
        await harness.FireLatestRetryAsync();

        harness.Agent.State.Released.Should().BeFalse();
        harness.Agent.State.PendingWrite!.Attempts.Should().Be(3);
        harness.Callbacks.Timeouts.Should().HaveCount(3);
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().NotBeNull();

        harness.Dispatcher.Recover();
        await harness.FireLatestRetryAsync();

        harness.Dispatcher.Documents.Should().ContainSingle().Which.Released.Should().BeTrue();
        harness.Agent.State.Released.Should().BeTrue();
        harness.Agent.State.PendingWrite.Should().BeNull();
        harness.Callbacks.Timeouts.Should().HaveCount(3);
    }

    [Fact]
    public async Task HandleRetryWriteAsync_AfterRelease_ShouldBeIgnored()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        var retry = UnpackRetryCommand(harness.Callbacks.Timeouts.Single());
        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());

        await harness.Agent.HandleRetryWriteAsync(retry);

        harness.Dispatcher.Documents.Should().BeEmpty();
        harness.Callbacks.Timeouts.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleEnsureAsync_AfterRelease_ShouldRestartWithNextGeneration()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        await harness.Agent.HandleReleaseAsync(BuildReleaseCommand());

        await harness.Agent.HandleEnsureAsync(BuildEnsureCommand());

        harness.Agent.State.Released.Should().BeFalse();
        harness.Agent.State.Active.Should().BeTrue();
        harness.Agent.State.ActivationGeneration.Should().Be(2);
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId))!
            .ActivationGeneration.Should().Be(2);
    }

    // ─── 9. Route policy ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(ProjectionScopeEnvelopeReceivedEvent), false)]
    [InlineData(typeof(ProjectionScopeEnvelopeAttemptedEvent), false)]
    [InlineData(typeof(ProjectionScopeObservationStagedEvent), false)]
    [InlineData(typeof(ProjectionScopeWatermarkAdvancedEvent), true)]
    [InlineData(typeof(ProjectionScopeDispatchFailedEvent), true)]
    [InlineData(typeof(ProjectionScopeStartedEvent), true)]
    [InlineData(typeof(ProjectionScopeReleasedEvent), true)]
    [InlineData(typeof(ProjectionScopeStatusRouteWarmingStartedEvent), true)]
    [InlineData(typeof(ProjectionScopeStatusRouteWarmingProbedEvent), true)]
    [InlineData(typeof(ProjectionScopeStatusRouteCaughtUpEvent), true)]
    [InlineData(typeof(ProjectionScopeStatusRouteBlockedEvent), true)]
    [InlineData(typeof(ProjectionScopeStatusRouteActivatedEvent), true)]
    [InlineData(typeof(ProjectionScopeStatusLegacyRouteReleasedEvent), true)]
    [InlineData(typeof(ProjectionScopeStatusRouteContractUpgradedEvent), true)]
    public void IsTerminalOutcome_ShouldClassifySourceEvents(System.Type sourceEventType, bool expected)
    {
        var sourceEvent = (IMessage)Activator.CreateInstance(sourceEventType)!;

        ProjectionScopeStatusRoutePolicy.IsTerminalOutcome(sourceEvent.Descriptor).Should().Be(expected);
        ProjectionScopeStatusRoutePolicy.IsTerminalOutcome(Any.Pack(sourceEvent)).Should().Be(expected);
    }

    [Fact]
    public void IsTerminalOutcome_WithoutEventData_ShouldBeFalse()
    {
        ProjectionScopeStatusRoutePolicy.IsTerminalOutcome((Any?)null).Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsTerminalOutcome(new Any()).Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsTerminalOutcome((Google.Protobuf.Reflection.MessageDescriptor?)null)
            .Should().BeFalse();
    }

    [Fact]
    public void BuildTerminalRoute_ShouldNameTerminalContractAndDefaultToActive()
    {
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(3);

        route.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2,
            "new routes are only ever created under the current terminal contract");
        route.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        route.ContractId.Should().NotBe(PreviousTerminalContractId);
        route.ContractVersion.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);
        route.ContractVersion.Should().Be(ProjectionScopeStatusGAgent.ContractVersion);
        route.ContractVersion.Should().BeGreaterThanOrEqualTo(1, "version 1 is the lowest valid route version");
        route.RouteEpoch.Should().Be(3);
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        route.LegacyRouteReleased.Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route).Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route).Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse();
    }

    [Fact]
    public void BuildLegacyRoute_ShouldNameLegacyContractAndDefaultToActive()
    {
        var route = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(4);

        route.ContractId.Should().Be(ProjectionScopeStatusRoutePolicy.LegacyContractId);
        route.ContractId.Should().Be("aevatar.projection.scope-status-legacy.v1");
        route.ContractVersion.Should().Be(ProjectionScopeStatusRoutePolicy.LegacyContractVersion);
        route.RouteEpoch.Should().Be(4);
        route.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active);
        ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route).Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route).Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified, true)]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming, false)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked, true)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active, true)]
    public void IsWritingPhase_ShouldTreatPhaselessBlockedAndActiveAsWriting(ProjectionScopeStatusRoutePhase phase, bool expected)
    {
        ProjectionScopeStatusRoutePolicy.IsWritingPhase(ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(0, ProjectionScopeStatusRoutePhase.Unspecified, true, false)]
    [InlineData(0, ProjectionScopeStatusRoutePhase.Warming, false, true)]
    [InlineData(0, ProjectionScopeStatusRoutePhase.Blocked, true, false)]
    [InlineData(0, ProjectionScopeStatusRoutePhase.Active, true, false)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Unspecified, false, false)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Warming, false, false)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Blocked, false, false)]
    [InlineData(1, ProjectionScopeStatusRoutePhase.Active, false, false)]
    public void TerminalRoutePredicates_ShouldCombineReaderVersionAndPhase(
        long contractVersionOffsetFromReader,
        ProjectionScopeStatusRoutePhase phase,
        bool expectedActive,
        bool expectedWarming)
    {
        // Offset 0: a route at this reader's own contract version; offset +1: a route of a
        // newer contract than this reader, which it never serves in any phase.
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase);
        route.ContractVersion = ProjectionScopeStatusGAgent.ContractVersion + contractVersionOffsetFromReader;

        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().Be(expectedActive);
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().Be(expectedWarming);
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified, true, false)]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming, false, true)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked, true, false)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active, true, false)]
    public void TerminalRoutePredicates_ShouldServeVersionOneRoutesOnEveryReader(
        ProjectionScopeStatusRoutePhase phase,
        bool expectedActive,
        bool expectedWarming)
    {
        // Version 1 is the lowest valid route version; every reader is at or above it, so v1
        // routes (the ones adopted by the first terminal binary) stay served across upgrades.
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase);
        route.ContractVersion = 1;

        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().Be(expectedActive);
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().Be(expectedWarming);
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion + 1)
            .Should().Be(expectedActive, "a future reader keeps serving v1 routes too");
    }

    [Fact]
    public void LegacyShadowMayWrite_ShouldSelectLegacyWriterByRouteAndPhase()
    {
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(null).Should().BeTrue("no route: pre-route source");
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(new ProjectionScopeStatusRoute()).Should().BeTrue("invalid route");
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(0))
            .Should().BeTrue("zero epoch is not a route");

        foreach (var phase in new[]
                 {
                     ProjectionScopeStatusRoutePhase.Unspecified,
                     ProjectionScopeStatusRoutePhase.Blocked,
                     ProjectionScopeStatusRoutePhase.Active,
                 })
        {
            ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, phase))
                .Should().BeTrue($"a legacy route in writing phase {phase} selects the legacy shadow");
            ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, phase))
                .Should().BeFalse($"a terminal route in writing phase {phase} selects the terminal");
            ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, phase))
                .Should().BeTrue();
            ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, phase))
                .Should().BeFalse();
        }

        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(
                ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, ProjectionScopeStatusRoutePhase.Warming))
            .Should().BeTrue("the legacy shadow keeps writing while the terminal warms");
        ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(
                ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, ProjectionScopeStatusRoutePhase.Warming))
            .Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(
                ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(2, ProjectionScopeStatusRoutePhase.Warming))
            .Should().BeFalse("during a rollback warming the terminal is still the writer");
        ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(null).Should().BeFalse();

        var newerTerminal = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2);
        newerTerminal.ContractVersion = ProjectionScopeStatusGAgent.ContractVersion + 1;
        ProjectionScopeStatusRoutePolicy.LegacyShadowMayWrite(newerTerminal).Should().BeFalse(
            "the gate is the route contract, not this binary's reader version");
    }

    [Fact]
    public void IsTerminalRoute_ShouldRequireEpochContractAndVersion()
    {
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(null).Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(new ProjectionScopeStatusRoute()).Should().BeFalse();

        var zeroEpoch = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(0);
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(zeroEpoch).Should().BeFalse();

        var blankContract = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1);
        blankContract.ContractId = " ";
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(blankContract).Should().BeFalse();

        var zeroVersion = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1);
        zeroVersion.ContractVersion = 0;
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(zeroVersion).Should().BeFalse();
    }

    [Fact]
    public void IsActiveTerminalRoute_ShouldRequireExactContractAndReaderAtOrAboveRouteVersion()
    {
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1);

        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                "other-contract",
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion + 1)
            .Should().BeTrue("a newer reader keeps serving routes of older contract versions");
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion - 1)
            .Should().BeFalse("an older reader cannot serve a route of a newer contract version");
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                null,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusRoutePhase.Unspecified, true, false)]
    [InlineData(ProjectionScopeStatusRoutePhase.Warming, false, true)]
    [InlineData(ProjectionScopeStatusRoutePhase.Blocked, true, false)]
    [InlineData(ProjectionScopeStatusRoutePhase.Active, true, false)]
    public void TerminalContractRevisions_ShouldServeThePreviousContractAndFenceNewerOnes(
        ProjectionScopeStatusRoutePhase phase,
        bool expectedActive,
        bool expectedWarming)
    {
        // The two directions of a mixed fleet: this materializer serves every route created under
        // an earlier terminal contract, and never a route created under a later one — neither a
        // later contract id nor a later revision of its own contract.
        var previousContract = BuildPreviousContractTerminalRoute(1, phase);
        var currentContract = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase);
        var futureRevision = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1, phase);
        futureRevision.ContractVersion = ProjectionScopeStatusGAgent.ContractVersion + 1;

        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(previousContract).Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                previousContract,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().Be(expectedActive);
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                previousContract,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().Be(expectedWarming);

        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                futureRevision,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse("a route of a newer revision of this contract names a reader this binary is not");
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                futureRevision,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeFalse();

        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                currentContract,
                PreviousTerminalContractId,
                PreviousTerminalContractVersion)
            .Should().BeFalse("a materializer of the previous contract never matches a current-contract route");
        ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
                currentContract,
                PreviousTerminalContractId,
                PreviousTerminalContractVersion)
            .Should().BeFalse();
    }

    [Fact]
    public void IsPreviousTerminalContractRoute_ShouldOnlyMatchRoutesOfAnEarlierTerminalContract()
    {
        ProjectionScopeStatusRoutePolicy.IsPreviousTerminalContractRoute(
                BuildPreviousContractTerminalRoute(1),
                ProjectionScopeStatusGAgent.ContractId)
            .Should().BeTrue("such a route is the one the source upgrades in place under a fresh admission");
        ProjectionScopeStatusRoutePolicy.IsPreviousTerminalContractRoute(
                ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1),
                ProjectionScopeStatusGAgent.ContractId)
            .Should().BeFalse("a route already on the current contract needs no upgrade");
        ProjectionScopeStatusRoutePolicy.IsPreviousTerminalContractRoute(
                ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(1),
                ProjectionScopeStatusGAgent.ContractId)
            .Should().BeFalse("the legacy shadow contract is not a terminal contract");
        ProjectionScopeStatusRoutePolicy.IsPreviousTerminalContractRoute(
                null,
                ProjectionScopeStatusGAgent.ContractId)
            .Should().BeFalse();
    }

    // ─── 10. Source scope state applier ──────────────────────────────────────

    [Fact]
    public void ApplyStatusRouteActivated_ShouldSetRouteAndResetLegacyReleaseFlag()
    {
        var current = new ProjectionScopeState { RootActorId = RootActorId };
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1);
        route.LegacyRouteReleased = true;
        var occurredAt = Timestamp.FromDateTimeOffset(FixedNow);

        var next = ProjectionScopeStateApplier.ApplyStatusRouteActivated(
            current,
            new ProjectionScopeStatusRouteActivatedEvent { Route = route, OccurredAtUtc = occurredAt });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.Should().BeNull("the applier is pure");
        next.StatusRoute.RouteEpoch.Should().Be(1);
        next.StatusRoute.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        next.StatusRoute.LegacyRouteReleased.Should().BeFalse("a fresh activation always starts with the legacy release pending");
        next.UpdatedAtUtc.Should().Be(occurredAt);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void ApplyStatusRouteActivated_WithLowerOrEqualEpoch_ShouldBeFenced(long currentEpoch, long incomingEpoch)
    {
        var current = new ProjectionScopeState
        {
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(currentEpoch),
        };
        current.StatusRoute.LegacyRouteReleased = true;

        var next = ProjectionScopeStateApplier.ApplyStatusRouteActivated(
            current,
            new ProjectionScopeStatusRouteActivatedEvent
            {
                Route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(incomingEpoch),
            });

        next.Should().BeSameAs(current);
        next.StatusRoute.RouteEpoch.Should().Be(currentEpoch);
        next.StatusRoute.LegacyRouteReleased.Should().BeTrue();
    }

    [Fact]
    public void ApplyStatusRouteActivated_WithHigherEpoch_ShouldMoveRoute()
    {
        var current = new ProjectionScopeState
        {
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1),
        };
        current.StatusRoute.LegacyRouteReleased = true;

        var next = ProjectionScopeStateApplier.ApplyStatusRouteActivated(
            current,
            new ProjectionScopeStatusRouteActivatedEvent
            {
                Route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2),
            });

        next.StatusRoute.RouteEpoch.Should().Be(2);
        next.StatusRoute.LegacyRouteReleased.Should().BeFalse();
    }

    [Fact]
    public void ApplyStatusRouteActivated_WithoutRoute_ShouldBeIgnored()
    {
        var current = new ProjectionScopeState();

        var next = ProjectionScopeStateApplier.ApplyStatusRouteActivated(
            current,
            new ProjectionScopeStatusRouteActivatedEvent());

        next.Should().BeSameAs(current);
        next.StatusRoute.Should().BeNull();
    }

    [Fact]
    public void ApplyStatusLegacyRouteReleased_ShouldOnlyMarkMatchingEpoch()
    {
        var current = new ProjectionScopeState
        {
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2),
        };
        var occurredAt = Timestamp.FromDateTimeOffset(FixedNow);

        var staleEpoch = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(
            current,
            new ProjectionScopeStatusLegacyRouteReleasedEvent { RouteEpoch = 1, OccurredAtUtc = occurredAt });
        staleEpoch.Should().BeSameAs(current);
        staleEpoch.StatusRoute.LegacyRouteReleased.Should().BeFalse();

        var futureEpoch = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(
            current,
            new ProjectionScopeStatusLegacyRouteReleasedEvent { RouteEpoch = 3, OccurredAtUtc = occurredAt });
        futureEpoch.Should().BeSameAs(current);

        var matching = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(
            current,
            new ProjectionScopeStatusLegacyRouteReleasedEvent { RouteEpoch = 2, OccurredAtUtc = occurredAt });
        matching.Should().NotBeSameAs(current);
        matching.StatusRoute.LegacyRouteReleased.Should().BeTrue();
        matching.StatusRoute.RouteEpoch.Should().Be(2);
        matching.UpdatedAtUtc.Should().Be(occurredAt);
        current.StatusRoute.LegacyRouteReleased.Should().BeFalse("the applier is pure");
    }

    [Fact]
    public void ApplyStatusRouteContractUpgraded_ShouldOnlyMoveATerminalWritingRouteForward()
    {
        // The in-place upgrade the terminal materializer's next write takes over on: same writer,
        // current contract, strictly higher epoch, phase forced Active. It must never apply over a
        // legacy route (a rollback owns the route), over a warming route (a cutover is in flight)
        // or at an epoch at or below the current one.
        var current = new ProjectionScopeState { StatusRoute = BuildPreviousContractTerminalRoute(1) };
        var upgraded = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, ProjectionScopeStatusRoutePhase.Blocked);
        var occurredAt = Timestamp.FromDateTimeOffset(FixedNow);

        var next = ProjectionScopeStateApplier.ApplyStatusRouteContractUpgraded(
            current,
            new ProjectionScopeStatusRouteContractUpgradedEvent { Route = upgraded, OccurredAtUtc = occurredAt });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.ContractId.Should().Be(PreviousTerminalContractId, "the applier is pure");
        next.StatusRoute.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        next.StatusRoute.RouteEpoch.Should().Be(2);
        next.StatusRoute.Phase.Should().Be(ProjectionScopeStatusRoutePhase.Active,
            "the writer is unchanged and authoritative throughout: there is no cutover to warm or block");
        next.UpdatedAtUtc.Should().Be(occurredAt);

        ProjectionScopeStateApplier.ApplyStatusRouteContractUpgraded(
                current,
                new ProjectionScopeStatusRouteContractUpgradedEvent
                {
                    Route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1),
                })
            .Should().BeSameAs(current, "an epoch at or below the current one is fenced");
        ProjectionScopeStateApplier.ApplyStatusRouteContractUpgraded(
                new ProjectionScopeState { StatusRoute = ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(1) },
                new ProjectionScopeStatusRouteContractUpgradedEvent { Route = upgraded })
            .StatusRoute.ContractId.Should().Be(ProjectionScopeStatusRoutePolicy.LegacyContractId,
                "a rolled-back scope is owned by the legacy shadow, not upgraded in place");
        ProjectionScopeStateApplier.ApplyStatusRouteContractUpgraded(
                new ProjectionScopeState
                {
                    StatusRoute = BuildPreviousContractTerminalRoute(1, ProjectionScopeStatusRoutePhase.Warming),
                },
                new ProjectionScopeStatusRouteContractUpgradedEvent { Route = upgraded })
            .StatusRoute.RouteEpoch.Should().Be(1, "a cutover in flight is never upgraded underneath");
        ProjectionScopeStateApplier.ApplyStatusRouteContractUpgraded(
                current,
                new ProjectionScopeStatusRouteContractUpgradedEvent())
            .Should().BeSameAs(current);
    }

    [Fact]
    public void ApplyStatusLegacyRouteReleased_WithoutRoute_ShouldBeIgnored()
    {
        var current = new ProjectionScopeState();

        var next = ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased(
            current,
            new ProjectionScopeStatusLegacyRouteReleasedEvent { RouteEpoch = 0 });

        next.Should().BeSameAs(current);
        next.StatusRoute.Should().BeNull();
    }

    // ─── 11. Shared document mapping ─────────────────────────────────────────

    [Fact]
    public void Map_ShouldBeByteIdenticalForSameInputs()
    {
        var state = BuildRoutedSourceState();
        state.LastSuccessfulVersion = 41;
        state.HighestSeenVersion = 42;
        state.ReceivedEnvelopeTotal = 10;
        state.HighestSeenVersionsByActor["publisher-b"] = 42;
        state.HighestSeenVersionsByActor["publisher-a"] = 40;
        state.LastSuccessfulVersionsByActor["publisher-a"] = 39;
        state.LastSuccessfulSourceCoordinatesByActor["publisher-b"] = new ProjectionSourceCoordinate
        {
            ActorId = "publisher-b",
            StateVersion = 42,
            EventId = "evt-b",
        };
        state.LastSuccessfulSourceCoordinatesByActor["publisher-a"] = new ProjectionSourceCoordinate
        {
            ActorId = "publisher-a",
            StateVersion = 39,
            EventId = "evt-a",
        };
        var stateEvent = BuildStateEvent(version: 42, eventId: "evt-42", new ProjectionScopeWatermarkAdvancedEvent());

        var first = ProjectionScopeStatusDocumentMapper.Map(state.Clone(), stateEvent.Clone(), FixedNow);
        var second = ProjectionScopeStatusDocumentMapper.Map(state.Clone(), stateEvent.Clone(), FixedNow);

        first.ToByteArray().Should().Equal(second.ToByteArray(),
            "both status writers must produce an exact duplicate at the same version under the same route, never a conflict");
        first.Id.Should().Be(SourceScopeActorId);
        first.ScopeActorId.Should().Be(SourceScopeActorId);
        first.StateVersion.Should().Be(42);
        first.LastEventId.Should().Be("evt-42");
        first.UpdatedAtUtcValue.Should().Be(Timestamp.FromDateTimeOffset(FixedNow));
        first.LastSuccessfulVersion.Should().Be(41);
        first.StatusRoute.Should().Be(state.StatusRoute, "the document carries the source's committed route");
        first.StatusRoute.Should().NotBeSameAs(state.StatusRoute, "as a copy");
        first.SourceVersions.Select(static source => source.SourceActorId).Should().Equal("publisher-a", "publisher-b");
        first.SourceVersions[0].VersionGap.Should().Be(1);
        first.LastSuccessfulSourceCoordinates.Select(static source => source.ActorId)
            .Should().Equal("publisher-a", "publisher-b");
        ProjectionWriteResultEvaluator.Evaluate(first, second).Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
    }

    [Fact]
    public void StatusDocument_ShouldCarryStatusRouteAsItsRouteEpochFence()
    {
        // The document carries the source scope's committed route (field 30) and exposes its
        // epoch as the write fence: at one source version a strictly higher epoch takes the
        // document over, equal epochs must be exact duplicates, a lower epoch is stale.
        var descriptor = ProjectionScopeStatusDocument.Descriptor;
        var field = descriptor.FindFieldByName("status_route");

        field.Should().NotBeNull();
        field!.FieldNumber.Should().Be(30);
        field.MessageType.Should().Be(ProjectionScopeStatusRoute.Descriptor);
        typeof(IProjectionRouteFencedReadModel).IsAssignableFrom(typeof(ProjectionScopeStatusDocument)).Should().BeTrue();

        var withoutRoute = new ProjectionScopeStatusDocument();
        ((IProjectionRouteFencedReadModel)withoutRoute).RouteEpoch.Should().Be(0,
            "a document written by a binary that did not carry the route has epoch 0");
        var withRoute = new ProjectionScopeStatusDocument
        {
            StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(7, ProjectionScopeStatusRoutePhase.Warming),
        };
        ((IProjectionRouteFencedReadModel)withRoute).RouteEpoch.Should().Be(7);
    }

    [Fact]
    public void Map_WithAndWithoutStatusRoute_ShouldDifferOnlyByTheRouteAndFence()
    {
        var routed = BuildRoutedSourceState();
        routed.LastSuccessfulVersion = 41;
        routed.HighestSeenVersion = 42;
        var unrouted = routed.Clone();
        unrouted.StatusRoute = null;
        var stateEvent = BuildStateEvent(version: 42, eventId: "evt-42", new ProjectionScopeWatermarkAdvancedEvent());

        var fromRouted = ProjectionScopeStatusDocumentMapper.Map(routed, stateEvent.Clone(), FixedNow);
        var fromUnrouted = ProjectionScopeStatusDocumentMapper.Map(unrouted, stateEvent.Clone(), FixedNow);

        fromRouted.ToByteArray().Should().NotEqual(fromUnrouted.ToByteArray(),
            "the route is part of the document now: it is the same-version write fence");
        fromRouted.StatusRoute.Should().Be(routed.StatusRoute);
        fromUnrouted.StatusRoute.Should().BeNull();
        ((IProjectionRouteFencedReadModel)fromRouted).RouteEpoch.Should().Be(1);
        ((IProjectionRouteFencedReadModel)fromUnrouted).RouteEpoch.Should().Be(0);
        var routeless = fromRouted.Clone();
        routeless.StatusRoute = null;
        routeless.ToByteArray().Should().Equal(fromUnrouted.ToByteArray(), "everything but the route is identical");
        ProjectionWriteResultEvaluator.Evaluate(fromUnrouted, fromRouted).Disposition
            .Should().Be(ProjectionWriteDisposition.Applied, "a routed write takes over a route-less document of the same version");
        ProjectionWriteResultEvaluator.Evaluate(fromRouted, fromUnrouted).Disposition
            .Should().Be(ProjectionWriteDisposition.Stale, "a route-less write never takes over a routed document of the same version");
    }

    [Fact]
    public void Map_WithDifferentRouteEpochs_ShouldProduceEpochFencedDocuments()
    {
        var epochOne = BuildRoutedSourceState();
        var epochTwo = BuildRoutedSourceState();
        epochTwo.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2);
        epochTwo.StatusRoute.LegacyRouteReleased = true;
        var stateEvent = BuildStateEvent(version: 7, eventId: "evt-7", new ProjectionScopeStatusRouteActivatedEvent());

        var first = ProjectionScopeStatusDocumentMapper.Map(epochOne, stateEvent.Clone(), FixedNow);
        var second = ProjectionScopeStatusDocumentMapper.Map(epochTwo, stateEvent.Clone(), FixedNow);

        first.ToByteArray().Should().NotEqual(second.ToByteArray());
        ((IProjectionRouteFencedReadModel)first).RouteEpoch.Should().Be(1);
        ((IProjectionRouteFencedReadModel)second).RouteEpoch.Should().Be(2);
        ProjectionWriteResultEvaluator.Evaluate(first, second).Disposition.Should().Be(ProjectionWriteDisposition.Applied);
        ProjectionWriteResultEvaluator.Evaluate(second, first).Disposition.Should().Be(ProjectionWriteDisposition.Stale);
        ProjectionWriteResultEvaluator.Evaluate(second, second.Clone()).Disposition.Should().Be(ProjectionWriteDisposition.Duplicate);
    }

    [Fact]
    public void Map_SameRouteDifferentPhaseFlags_ShouldConflictAtEqualEpoch()
    {
        // Equal epochs fall through to the strict identity rules: the same source version can
        // only ever have one committed route, so differing bytes at an equal epoch are a conflict.
        var blocked = BuildSourceState();
        blocked.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, ProjectionScopeStatusRoutePhase.Blocked);
        var active = BuildSourceState();
        active.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2, ProjectionScopeStatusRoutePhase.Active);
        var stateEvent = BuildStateEvent(version: 7, eventId: "evt-7", new ProjectionScopeWatermarkAdvancedEvent());

        var first = ProjectionScopeStatusDocumentMapper.Map(blocked, stateEvent.Clone(), FixedNow);
        var second = ProjectionScopeStatusDocumentMapper.Map(active, stateEvent.Clone(), FixedNow);

        ProjectionWriteResultEvaluator.Evaluate(first, second).Disposition.Should().Be(ProjectionWriteDisposition.Conflict);
    }

    [Fact]
    public async Task TerminalWrite_ShouldMatchSharedMapperBytesIncludingRoute()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2);
        sourceState.StatusRoute.LegacyRouteReleased = true;
        sourceState.StatusRoute.FlipVersion = 40;
        var stateEvent = BuildStateEvent(version: 42, eventId: "evt-42", new ProjectionScopeWatermarkAdvancedEvent());

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(sourceState, stateEvent));

        var expected = ProjectionScopeStatusDocumentMapper.Map(
            sourceState,
            stateEvent,
            stateEvent.Timestamp.ToDateTimeOffset());
        var written = harness.Dispatcher.Documents.Should().ContainSingle().Subject;
        written.ToByteArray().Should().Equal(expected.ToByteArray());
        written.StatusRoute.Should().Be(sourceState.StatusRoute);
        ((IProjectionRouteFencedReadModel)written).RouteEpoch.Should().Be(2);
    }

    [Fact]
    public async Task TerminalWrite_AndLegacyShadowWrite_ShouldBeByteIdenticalForSameStateEventAndTimestamp()
    {
        // Both writers share one mapper and both map the source's committed route: for the same
        // state, state event and resolved timestamp their documents are the same bytes (the
        // legacy shadow's gate is bypassed here on purpose; see the projector tests for the gate).
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2);
        sourceState.HighestSeenVersion = 42;
        sourceState.LastSuccessfulVersion = 41;
        var stateEvent = BuildStateEvent(version: 42, eventId: "evt-42", new ProjectionScopeWatermarkAdvancedEvent());
        var envelope = BuildForwardedEnvelope(sourceState, stateEvent);

        await harness.Agent.HandleObservedEnvelopeAsync(envelope);

        CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var unpackedEvent,
                out var unpackedState)
            .Should().BeTrue();
        var legacyShadowWrite = ProjectionScopeStatusDocumentMapper.Map(
            unpackedState!,
            unpackedEvent!,
            CommittedStateEventEnvelope.ResolveTimestamp(envelope, FixedNow));
        var terminalWrite = harness.Dispatcher.Documents.Should().ContainSingle().Subject;
        terminalWrite.ToByteArray().Should().Equal(legacyShadowWrite.ToByteArray());
        ProjectionWriteResultEvaluator.Evaluate(legacyShadowWrite, terminalWrite).Disposition
            .Should().Be(ProjectionWriteDisposition.Duplicate);
        ProjectionWriteResultEvaluator.Evaluate(terminalWrite, legacyShadowWrite).Disposition
            .Should().Be(ProjectionWriteDisposition.Duplicate);
    }

    // ─── builders ────────────────────────────────────────────────────────────

    private static EnsureProjectionScopeCommand BuildEnsureCommand() =>
        new()
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            Mode = ProjectionScopeMode.DurableMaterialization,
        };

    /// <summary>
    /// A lifecycle release. <paramref name="statusRouteEpoch"/> &gt; 0 marks it the previous-writer
    /// release of a status-route cutover at that epoch, which must be confirmed to the source.
    /// </summary>
    private static ReleaseProjectionScopeCommand BuildReleaseCommand(
        long statusRouteEpoch = 0,
        long requiredObservedVersion = 0) =>
        new()
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            Mode = ProjectionScopeMode.DurableMaterialization,
            StatusRouteEpoch = statusRouteEpoch,
            ExpectedWriterActorId = statusRouteEpoch > 0 ? TerminalActorId : string.Empty,
            RequiredObservedVersion = statusRouteEpoch > 0 ? requiredObservedVersion : 0,
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
            QuiescedAt = Timestamp.FromDateTimeOffset(FixedNow),
            QuiescenceTransitionId = "transition:quiesce-v2",
        };

    private static RuntimeFleetCapabilityAdmission CreateActivationSealAdmission()
    {
        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            Status = RuntimeFleetCapabilityGateStatus.Open,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 9,
            CapabilityEpoch = 3,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            MinimumReaderContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
            MembershipObservedAt = Timestamp.FromDateTimeOffset(FixedNow.AddSeconds(-5)),
            MembershipValidUntil = Timestamp.FromDateTimeOffset(FixedNow.AddMinutes(1)),
            ActiveMemberCount = 1,
            ConfirmedMemberCount = 1,
            MembershipDigest = "digest-a",
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
        };
        admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember
        {
            MemberId = "member-a",
            Incarnation = "inc-a",
        });
        return admission;
    }

    private static RuntimeActorStateSchemaAdoptionReceipt CreateActivationSealReceipt() =>
        new()
        {
            StateSchemaVersion = 1,
            RequiredCapability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            RequiredContractId =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
            RequiredContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
            CapabilityEpoch = 3,
            AuthorityStateVersion = 9,
            MembershipEpoch = 7,
            MembershipDigest = "digest-a",
            DeploymentRevision = "revision-a",
            AdoptedAt = Timestamp.FromDateTimeOffset(FixedNow.AddSeconds(-1)),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            EvidenceStatus = RuntimeFleetCapabilityGateStatus.Open,
        };

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

    private static void AddPhaseBSeals(ProjectionScopeStatusRoute route)
    {
        route.ActivationSeals.Clear();
        route.ActivationSeals.Add(CreateActivationSeal(
            ProjectionScopeStatusActorRole.Source,
            SourceScopeActorId,
            "projection.materialization-scope.test-context"));
        route.ActivationSeals.Add(CreateActivationSeal(
            ProjectionScopeStatusActorRole.LegacyWriter,
            LegacyActorId,
            LegacyWriterAgentKind));
        route.ActivationSeals.Add(CreateActivationSeal(
            ProjectionScopeStatusActorRole.TerminalWriter,
            TerminalActorId,
            ProjectionScopeStatusGAgent.AgentKind));
    }

    private static Task DispatchReleaseAsync(
        ProjectionScopeStatusGAgent agent,
        ReleaseProjectionScopeCommand command,
        string? publisherActorId = null)
    {
        var publisher = publisherActorId ?? SourceScopeActorId;
        return agent.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateDirect(publisher, TerminalActorId),
            Runtime = new EnvelopeRuntime { SourceActorId = publisher },
            Payload = Any.Pack(command),
        });
    }

    private static RequestProjectionScopeStatusActorSealCommand BuildSealRequest(
        ProjectionScopeStatusActorRole role,
        string expectedActorId,
        string expectedAgentKind,
        long routeEpoch) =>
        new()
        {
            SourceScopeActorId = SourceScopeActorId,
            RouteEpoch = routeEpoch,
            Role = role,
            ExpectedActorId = expectedActorId,
            ExpectedAgentKind = expectedAgentKind,
        };

    private static Task DispatchSealRequestAsync(
        ProjectionScopeStatusGAgent agent,
        RequestProjectionScopeStatusActorSealCommand command,
        string publisherActorId,
        bool includeRuntimeSource = true,
        string? runtimeSourceActorId = null,
        bool direct = true) =>
        agent.HandleEventAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = direct
                ? EnvelopeRouteSemantics.CreateDirect(publisherActorId, TerminalActorId)
                : EnvelopeRouteSemantics.CreateTopologyPublication(
                    publisherActorId,
                    TopologyAudience.Children),
            Runtime = includeRuntimeSource
                ? new EnvelopeRuntime { SourceActorId = runtimeSourceActorId ?? publisherActorId }
                : null,
            Payload = Any.Pack(command),
        });

    private static ProjectionScopeState BuildSourceState() =>
        new()
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            Released = false,
            ObservationAttached = true,
        };

    private static ProjectionScopeState BuildRoutedSourceState()
    {
        var state = BuildSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1);
        return state;
    }

    /// <summary>
    /// A route as an older source binary committed it: the previous terminal contract at its own
    /// reader version. The current materializer serves it so a source keeps its writer across the
    /// contract upgrade; no new route is ever created under it.
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

    private static StateEvent BuildStateEvent(long version, string eventId, IMessage sourceEvent) =>
        new()
        {
            EventId = eventId,
            Version = version,
            EventType = sourceEvent.Descriptor.FullName,
            EventData = Any.Pack(sourceEvent),
            AgentId = SourceScopeActorId,
            Timestamp = Timestamp.FromDateTimeOffset(FixedNow.AddMinutes(version)),
        };

    private static EventEnvelope BuildForwardedEnvelope(
        ProjectionScopeState sourceState,
        StateEvent stateEvent,
        string? targetStreamId = null)
    {
        var original = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(SourceScopeActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = stateEvent,
                StateRoot = Any.Pack(sourceState),
            }),
        };

        return StreamForwardingRules.BuildForwardedEnvelope(
            original,
            sourceStreamId: SourceScopeActorId,
            targetStreamId: targetStreamId ?? TerminalActorId,
            StreamForwardingMode.HandleThenForward);
    }

    /// <summary>
    /// A durable retry "fires" when the callback scheduler delivers the recorded trigger envelope
    /// to the actor's own inbox; in tests that is the unpacked self command handed to the handler.
    /// </summary>
    private static RetryProjectionScopeStatusWriteCommand UnpackRetryCommand(RuntimeCallbackTimeoutRequest timeout)
    {
        timeout.TriggerEnvelope.Route.Should().NotBeNull();
        timeout.TriggerEnvelope.Payload.Is(RetryProjectionScopeStatusWriteCommand.Descriptor).Should().BeTrue(
            "the durable trigger carries the retry command itself");
        return timeout.TriggerEnvelope.Payload.Unpack<RetryProjectionScopeStatusWriteCommand>();
    }

    // ─── harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One terminal materializer's world: the recorded status writes, the source stream's
    /// relay registry, the actor's own durable event log (survives actor replacement), the
    /// durable callback scheduler that owns its backed-off retries, the failure alert sink
    /// and a recording publisher that proves nothing is ever self-published inline.
    /// </summary>
    private sealed class TerminalHarness
    {
        private readonly IServiceProvider _services;

        public TerminalHarness(
            bool withCallbackScheduler = true,
            bool withAlertSink = true,
            RuntimeFleetCapabilityQuiescenceEvidence? quiescence = null,
            bool phaseBReady = false)
        {
            EventSourcing = new TerminalEventSourcing(Interactions);
            Outbox = new RecordingEventPublisher(Interactions);
            var services = new ServiceCollection()
                .AddSingleton<IStreamProvider>(Streams)
                .AddSingleton<IAgentKindRegistry>(new AgentKindRegistry(
                [
                    ProjectionScopeAgentRegistration.Create<ProjectionScopeStatusGAgent>(),
                ]))
                .AddSingleton<IProjectionWriteDispatcher<ProjectionScopeStatusDocument>>(Dispatcher)
                .AddSingleton<IProjectionClock>(new FixedProjectionClock(FixedNow))
                .AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
            if (withCallbackScheduler)
                services.AddSingleton<IActorRuntimeCallbackScheduler>(Callbacks);
            if (withAlertSink)
                services.AddSingleton<IProjectionFailureAlertSink>(Alerts);
            if (phaseBReady)
            {
                var fleet = new StaticPhaseBFleetReader(
                    CreateActivationSealAdmission(),
                    quiescence);
                services.AddSingleton<IRuntimeFleetCapabilityAdmissionReader>(fleet);
                services.AddSingleton<IRuntimeLocalMembershipIdentityReader>(fleet);
                services.AddSingleton<IRuntimeFleetCapabilityQuiescenceReader>(fleet);
                services.AddSingleton<IRuntimeActorStateSchemaContextReader>(
                    new StaticRuntimeActorStateSchemaContextReader(new RuntimeActorStateSchemaContext(
                        ProjectionScopeStatusGAgent.AgentKind,
                        StateSchemaVersion: 1,
                        [CreateActivationSealReceipt()])));
            }
            else if (quiescence != null)
            {
                services.AddSingleton<IRuntimeFleetCapabilityQuiescenceReader>(
                    new StaticQuiescenceReader(quiescence));
            }
            _services = services.BuildServiceProvider();
            Agent = CreateReplacementActor();
        }

        public RecordingStreamProvider Streams { get; } = new();
        public RecordingStatusWriteDispatcher Dispatcher { get; } = new();

        /// <summary>
        /// The one tick the durable log and the outbox share, so the ORDER of "committed" and
        /// "sent to the source" is observable across the two recorders.
        /// </summary>
        public TerminalInteractionSequence Interactions { get; } = new();

        public TerminalEventSourcing EventSourcing { get; }
        public RecordingEventPublisher Outbox { get; }
        public RecordingCallbackScheduler Callbacks { get; } = new();
        public RecordingFailureAlertSink Alerts { get; } = new();
        public ProjectionScopeStatusGAgent Agent { get; }

        public static async Task<TerminalHarness> CreateStartedAsync(
            bool withCallbackScheduler = true,
            bool withAlertSink = true,
            RuntimeFleetCapabilityQuiescenceEvidence? quiescence = null,
            bool phaseBReady = false)
        {
            var harness = new TerminalHarness(
                withCallbackScheduler,
                withAlertSink,
                quiescence,
                phaseBReady);
            await harness.Agent.HandleEnsureAsync(BuildEnsureCommand());
            harness.Agent.State.Active.Should().BeTrue();
            return harness;
        }

        private sealed class StaticQuiescenceReader(RuntimeFleetCapabilityQuiescenceEvidence evidence)
            : IRuntimeFleetCapabilityQuiescenceReader
        {
            public Task<RuntimeFleetCapabilityQuiescenceEvidence?> GetQuiescenceAsync(
                RuntimeFleetCapability capability,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<RuntimeFleetCapabilityQuiescenceEvidence?>(
                    evidence.Capability == capability ? evidence.Clone() : null);
            }
        }

        private sealed class StaticPhaseBFleetReader(
            RuntimeFleetCapabilityAdmission admission,
            RuntimeFleetCapabilityQuiescenceEvidence? quiescence)
            : IRuntimeFleetCapabilityAdmissionReader,
                IRuntimeLocalMembershipIdentityReader,
                IRuntimeFleetCapabilityQuiescenceReader
        {
            public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
                RuntimeFleetCapability capability,
                CancellationToken ct = default) =>
                Task.FromResult<RuntimeFleetCapabilityAdmission?>(
                    capability == admission.Capability ? admission.Clone() : null);

            public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(
                CancellationToken ct = default) =>
                ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(new RuntimeLocalMembershipIdentity(
                    7,
                    "digest-a",
                    "revision-a",
                    "member-a",
                    "inc-a"));

            public Task<RuntimeFleetCapabilityQuiescenceEvidence?> GetQuiescenceAsync(
                RuntimeFleetCapability capability,
                CancellationToken ct = default) =>
                Task.FromResult<RuntimeFleetCapabilityQuiescenceEvidence?>(
                    quiescence?.Capability == capability ? quiescence.Clone() : null);
        }

        private sealed class StaticRuntimeActorStateSchemaContextReader(
            RuntimeActorStateSchemaContext context)
            : IRuntimeActorStateSchemaContextReader
        {
            public RuntimeActorStateSchemaContext? Current { get; } = context;
        }

        /// <summary>
        /// Fires the most recently scheduled durable retry against the current actor: the
        /// scheduler delivers the trigger envelope to the actor's inbox once its due time elapses.
        /// </summary>
        public Task FireLatestRetryAsync() =>
            Agent.HandleRetryWriteAsync(UnpackRetryCommand(Callbacks.Timeouts[^1]));

        /// <summary>
        /// Appends a deferred-write fact to the durable log the way the previous binary wrote
        /// it: no failure kind (Unspecified), no stalled flag, no next-retry stamp.
        /// </summary>
        public async Task<ProjectionScopeStatusPendingWrite> SeedPendingWriteFromPreviousBinaryAsync(
            EventEnvelope envelope,
            int attempts,
            ProjectionScopeStatusWriteFailureKind failureKind = ProjectionScopeStatusWriteFailureKind.Unspecified)
        {
            CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(envelope, out _, out var stateEvent, out _)
                .Should().BeTrue();
            var pending = new ProjectionScopeStatusPendingWrite
            {
                Source = new ProjectionSourceCoordinate
                {
                    ActorId = SourceScopeActorId,
                    StateVersion = stateEvent!.Version,
                    EventId = stateEvent.EventId,
                },
                Envelope = envelope.Clone(),
                Attempts = attempts,
                LastError = nameof(IOException),
                DeferredAtUtc = Timestamp.FromDateTimeOffset(FixedNow.AddMinutes(-1)),
                FailureKind = failureKind,
            };
            EventSourcing.RaiseEvent(new ProjectionScopeStatusWriteDeferredEvent
            {
                Pending = pending.Clone(),
                OccurredAtUtc = pending.DeferredAtUtc,
            });
            await EventSourcing.ConfirmEventsAsync();
            return pending;
        }

        /// <summary>A fresh actor instance over the same durable log, as after a host restart.</summary>
        public ProjectionScopeStatusGAgent CreateReplacementActor()
        {
            var agent = new ProjectionScopeStatusGAgent
            {
                Services = _services,
                EventPublisher = Outbox,
                EventSourcing = EventSourcing,
            };
            typeof(GAgentBase)
                .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(agent, TerminalActorId);
            return agent;
        }
    }

    /// <summary>
    /// A monotonic interaction tick handed to the recorders that must be ordered against each
    /// other (the durable event log and the outbox). Deterministic: it advances only when the
    /// actor commits or sends, never with wall-clock time.
    /// </summary>
    private sealed class TerminalInteractionSequence
    {
        private int _tick;

        public int Next() => ++_tick;
    }

    private sealed class TerminalEventSourcing(TerminalInteractionSequence interactions)
        : IEventSourcingBehavior<ProjectionScopeStatusTerminalState>
    {
        private readonly List<IMessage> _pending = [];
        private ProjectionScopeStatusTerminalState _durableState = new();

        public List<IMessage> PersistedEvents { get; } = [];

        /// <summary>The interaction tick of each committed event, by the same index.</summary>
        public List<int> PersistedEventTicks { get; } = [];

        public long CurrentVersion { get; private set; }

        /// <summary>The interaction tick at which <paramref name="evt"/> was committed.</summary>
        public int TickOfCommitted(IMessage evt)
        {
            // By identity: two protobuf events of the same shape compare equal by value.
            var index = PersistedEvents.FindIndex(persisted => ReferenceEquals(persisted, evt));
            index.Should().BeGreaterThanOrEqualTo(0, "the event must have been committed by this actor");
            return PersistedEventTicks[index];
        }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
                PersistedEvents.Add(evt);
                PersistedEventTicks.Add(interactions.Next());
                _durableState = TransitionState(_durableState, evt);
                result.CommittedEvents.Add(new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = ++CurrentVersion,
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                });
            }

            result.LatestVersion = CurrentVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(ProjectionScopeStatusTerminalState currentState, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ProjectionScopeStatusTerminalState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<ProjectionScopeStatusTerminalState?>(
                PersistedEvents.Count == 0 ? null : _durableState.Clone());

        public void DiscardPendingEvents() => _pending.Clear();

        public ProjectionScopeStatusTerminalState TransitionState(ProjectionScopeStatusTerminalState current, IMessage evt) =>
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

    /// <summary>
    /// The status write dispatcher double. By default every write is accepted as Applied
    /// (scripted outcomes and outages can be queued); backed by an in-memory document store it
    /// returns the store's real evaluator disposition, so the document's route-epoch fence is
    /// exercised end to end.
    /// </summary>
    private sealed class RecordingStatusWriteDispatcher : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
    {
        private readonly Queue<object> _outcomes = new();
        private Exception? _alwaysThrow;
        private InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>? _store;

        public List<ProjectionScopeStatusDocument> Documents { get; } = [];
        public List<(long Version, long RouteEpoch, ProjectionWriteDisposition Disposition)> Writes { get; } = [];
        public int UpsertAttempts { get; private set; }

        public void Enqueue(ProjectionWriteResult result) => _outcomes.Enqueue(result);

        public void Enqueue(Exception exception) => _outcomes.Enqueue(exception);

        public void AlwaysThrow(Exception exception) => _alwaysThrow = exception;

        /// <summary>The store is back: writes succeed again (queued outcomes still apply first).</summary>
        public void Recover() => _alwaysThrow = null;

        /// <summary>Unscripted writes go through the real in-memory store (and its write evaluator).</summary>
        public void BackBy(InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string> store) => _store = store;

        public async Task<ProjectionWriteResult> UpsertAsync(ProjectionScopeStatusDocument readModel, CancellationToken ct = default)
        {
            UpsertAttempts++;
            if (_alwaysThrow != null)
                throw _alwaysThrow;

            ProjectionWriteResult result;
            if (_outcomes.Count > 0)
            {
                var outcome = _outcomes.Dequeue();
                if (outcome is Exception exception)
                    throw exception;

                result = (ProjectionWriteResult)outcome;
            }
            else if (_store != null)
            {
                result = await _store.UpsertAsync(readModel, ct);
            }
            else
            {
                result = ProjectionWriteResult.Applied();
            }

            Documents.Add(readModel.Clone());
            Writes.Add((readModel.StateVersion, ((IProjectionRouteFencedReadModel)readModel).RouteEpoch, result.Disposition));
            return result;
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Records anything the actor publishes inline. The terminal materializer never
    /// self-publishes (its only continuation is the durable callback retry); the one message
    /// it addresses to another actor is the warming caught-up report sent to its source scope.
    /// </summary>
    private sealed class RecordingEventPublisher(TerminalInteractionSequence interactions) : IEventPublisher
    {
        public List<PublishedMessage> Published { get; } = [];
        public List<SentMessage> Sent { get; } = [];

        public IReadOnlyList<SentMessage> CaughtUpReports =>
            Sent.Where(static sent => sent.Message is ProjectionScopeStatusWriterCaughtUpEvent).ToList();

        /// <summary>
        /// The typed previous-writer release confirmations: the source flips its route on these,
        /// never on inbox acceptance of the release command.
        /// </summary>
        public IReadOnlyList<SentMessage> ReleaseConfirmations =>
            Sent.Where(static sent => sent.Message is ProjectionScopeStatusWriterReleasedEvent).ToList();

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            Published.Add(new PublishedMessage(evt, audience));
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
            Sent.Add(new SentMessage(targetActorId, evt, interactions.Next()));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedMessage(IMessage Message, TopologyAudience Audience);

    /// <summary><paramref name="Tick"/> orders this send against the actor's committed events.</summary>
    private sealed record SentMessage(string TargetActorId, IMessage Message, int Tick)
    {
        public ProjectionScopeStatusWriterCaughtUpEvent CaughtUp =>
            Message.Should().BeOfType<ProjectionScopeStatusWriterCaughtUpEvent>().Subject;

        public ProjectionScopeStatusWriterReleasedEvent ReleaseConfirmation =>
            Message.Should().BeOfType<ProjectionScopeStatusWriterReleasedEvent>().Subject;

        /// <summary>
        /// The typed views above assert on the message type, so the compiler-generated
        /// <see cref="object.ToString"/> would THROW while rendering a mixed collection — turning
        /// any failing outbox assertion into a misleading "expected type" error. Print the facts.
        /// </summary>
        private bool PrintMembers(StringBuilder builder)
        {
            builder.Append(
                $"TargetActorId = {TargetActorId}, Message = {Message.Descriptor.FullName} {Message}, Tick = {Tick}");
            return true;
        }
    }

    /// <summary>
    /// Records every durable timeout the actor schedules. Firing is explicit in tests: the
    /// recorded trigger envelope is unpacked and handed to the actor's retry handler.
    /// </summary>
    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The terminal materializer only schedules one-shot timeouts.");

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingFailureAlertSink : IProjectionFailureAlertSink
    {
        private Exception? _throwOnPublish;

        public List<ProjectionFailureAlert> Published { get; } = [];

        public void ThrowOnPublish(Exception exception) => _throwOnPublish = exception;

        public Task PublishAsync(ProjectionFailureAlert alert, CancellationToken ct = default)
        {
            if (_throwOnPublish != null)
                throw _throwOnPublish;

            Published.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly Dictionary<string, RecordingStream> _streams = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> StreamIds => _streams.Keys;

        public RecordingStream GetStream(string actorId)
        {
            if (!_streams.TryGetValue(actorId, out var stream))
            {
                stream = new RecordingStream(actorId);
                _streams[actorId] = stream;
            }

            return stream;
        }

        IStream IStreamProvider.GetStream(string actorId) => GetStream(actorId);

        public Task<StreamForwardingBinding?> GetBindingAsync(string sourceStreamId, string targetStreamId) =>
            Task.FromResult(
                _streams.TryGetValue(sourceStreamId, out var stream) &&
                stream.Relays.TryGetValue(targetStreamId, out var binding)
                    ? binding
                    : null);
    }

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId { get; } = streamId;
        public Dictionary<string, StreamForwardingBinding> Relays { get; } = new(StringComparer.Ordinal);
        public List<string> RemovedTargets { get; } = [];
        public int UpsertCount { get; private set; }

        public Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage => throw new NotSupportedException();

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new() => throw new NotSupportedException();

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            binding.SourceStreamId.Should().Be(StreamId, "a relay is always written on its source stream");
            UpsertCount++;
            Relays[binding.TargetStreamId] = binding;
            return Task.CompletedTask;
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            RemovedTargets.Add(targetStreamId);
            Relays.Remove(targetStreamId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(Relays.Values.ToList());
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
