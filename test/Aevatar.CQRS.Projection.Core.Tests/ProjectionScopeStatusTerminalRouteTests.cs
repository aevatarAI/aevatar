using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.Orchestration;
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
/// deferred-write retry through the callback scheduler, and the source-owned status route
/// that decides who may write.
/// </summary>
public sealed class ProjectionScopeStatusTerminalRouteTests
{
    private const string RootActorId = "root-actor";
    private const string ProjectionKind = "test-kind";
    private const string TerminalWriteAlertStage = "terminal-status-write";
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan CappedRetryDelay = TimeSpan.FromMinutes(10);

    private static readonly string SourceScopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
        RootActorId,
        ProjectionKind,
        ProjectionRuntimeMode.DurableMaterialization));

    private static readonly string TerminalActorId =
        ProjectionScopeStatusRoutes.BuildTerminalActorId(SourceScopeActorId);

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

    // ─── 4. Legacy / foreign route → observe only ────────────────────────────

    [Fact]
    public async Task HandleObservedEnvelopeAsync_LegacyRoute_ShouldObserveOnly()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = null;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().BeEmpty("the legacy shadow is still the writer");
        harness.EventSourcing.PersistedEvents.Should().ContainSingle();
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

    [Fact]
    public async Task HandleObservedEnvelopeAsync_ForeignContractVersion_ShouldObserveOnly()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildSourceState();
        sourceState.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(1);
        sourceState.StatusRoute.ContractVersion = ProjectionScopeStatusGAgent.ContractVersion + 1;

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            sourceState,
            BuildStateEvent(version: 5, eventId: "evt-5", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().BeEmpty();
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
    public async Task ActivateAsync_WithRejectedPendingWrite_ShouldNotScheduleRetry()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(ProjectionWriteDisposition.Conflict));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Agent.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Alerts.Published.Clear();

        var reactivated = harness.CreateReplacementActor();
        await reactivated.ActivateAsync();

        reactivated.State.PendingWrite.Should().NotBeNull();
        reactivated.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.Callbacks.Timeouts.Should().BeEmpty("retrying identical bytes cannot heal a rejected write");
        harness.Alerts.Published.Should().BeEmpty("activation does not re-alert");
        harness.Outbox.Published.Should().BeEmpty();
        (await harness.Streams.GetBindingAsync(SourceScopeActorId, TerminalActorId)).Should().NotBeNull();
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

    // ─── 7. Dispositions ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProjectionWriteDisposition.Conflict)]
    [InlineData(ProjectionWriteDisposition.Gap)]
    public async Task HandleObservedEnvelopeAsync_RejectedDisposition_ShouldRecordRejectedPendingAlertAndNotRetry(
        ProjectionWriteDisposition disposition)
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(disposition));

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        await act.Should().NotThrowAsync();
        var deferred = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>()
            .Should().ContainSingle().Subject;
        deferred.Pending.LastError.Should().Be(disposition.ToString());
        deferred.Pending.Attempts.Should().Be(1);
        deferred.Pending.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        deferred.Pending.Stalled.Should().BeFalse();
        deferred.Pending.NextRetryAtUtc.Should().BeNull("a rejected write has no retry");
        deferred.Pending.Source.StateVersion.Should().Be(12);
        harness.Agent.State.PendingWrite.Should().Be(deferred.Pending);
        harness.Callbacks.Timeouts.Should().BeEmpty("retrying identical bytes cannot heal a rejected write");
        harness.Outbox.Published.Should().BeEmpty();
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteStalledEvent>().Should().BeEmpty();
        var alert = harness.Alerts.Published.Should().ContainSingle().Subject;
        alert.Stage.Should().Be(TerminalWriteAlertStage);
        alert.Reason.Should().Be(disposition.ToString());
        alert.SourceVersion.Should().Be(12);
        alert.EventId.Should().Be("evt-12");
        alert.FailureId.Should().Be($"{SourceScopeActorId}:12:evt-12");
        alert.EventType.Should().Be(nameof(ProjectionScopeStatusDocument));
        alert.UnresolvedFailureCount.Should().Be(1);
    }

    [Theory]
    [InlineData(ProjectionWriteDisposition.Conflict)]
    [InlineData(ProjectionWriteDisposition.Gap)]
    public async Task HandleRetryWriteAsync_ForRejectedPending_ShouldBeNoOp_AndHigherVersionWriteShouldClearIt(
        ProjectionWriteDisposition disposition)
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(disposition));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        var pending = harness.Agent.State.PendingWrite!;
        var eventsBefore = harness.EventSourcing.PersistedEvents.Count;
        var attemptsBefore = harness.Dispatcher.UpsertAttempts;

        await harness.Agent.HandleRetryWriteAsync(new RetryProjectionScopeStatusWriteCommand
        {
            ExpectedSource = pending.Source.Clone(),
            Attempt = pending.Attempts,
        });

        harness.Dispatcher.UpsertAttempts.Should().Be(attemptsBefore, "a rejected pending write is never retried");
        harness.EventSourcing.PersistedEvents.Should().HaveCount(eventsBefore);
        harness.Agent.State.PendingWrite.Should().Be(pending);
        harness.Callbacks.Timeouts.Should().BeEmpty();
        harness.Alerts.Published.Should().ContainSingle();

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 13, eventId: "evt-13", new ProjectionScopeWatermarkAdvancedEvent())));

        harness.Dispatcher.Documents.Should().HaveCount(2).And.Subject.Last().StateVersion.Should().Be(13);
        var recovered = harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteRecoveredEvent>()
            .Should().ContainSingle().Subject;
        recovered.Source.StateVersion.Should().Be(13);
        harness.Agent.State.PendingWrite.Should().BeNull("a later higher-version terminal write supersedes the rejection");
        harness.Callbacks.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_RejectedDisposition_WithoutAlertSink_ShouldStillRecordRejectedPending()
    {
        var harness = await TerminalHarness.CreateStartedAsync(withAlertSink: false);
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(ProjectionWriteDisposition.Conflict));

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        await act.Should().NotThrowAsync();
        harness.Agent.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.Callbacks.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleObservedEnvelopeAsync_WhenAlertSinkThrows_ShouldStillRecordRejectedPending()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Alerts.ThrowOnPublish(new InvalidOperationException("alert channel down"));
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(ProjectionWriteDisposition.Gap));

        var act = () => harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));

        await act.Should().NotThrowAsync("the alert is best-effort; the durable fact is the deferral");
        harness.Agent.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.EventSourcing.PersistedEvents.OfType<ProjectionScopeStatusWriteDeferredEvent>().Should().ContainSingle();
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

    [Fact]
    public async Task HandleRetryWriteAsync_WhenRetryIsRejected_ShouldTurnTransientPendingIntoRejected()
    {
        // The store comes back but another writer already committed different bytes at this
        // version: the retry ends in an explicit rejected state instead of retrying forever.
        var harness = await TerminalHarness.CreateStartedAsync();
        harness.Dispatcher.Enqueue(new IOException("store unavailable"));
        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(
            BuildRoutedSourceState(),
            BuildStateEvent(version: 12, eventId: "evt-12", new ProjectionScopeWatermarkAdvancedEvent())));
        harness.Dispatcher.Enqueue(new ProjectionWriteResult(ProjectionWriteDisposition.Conflict));

        await harness.FireLatestRetryAsync();

        harness.Agent.State.PendingWrite!.FailureKind.Should().Be(ProjectionScopeStatusWriteFailureKind.Rejected);
        harness.Agent.State.PendingWrite.Attempts.Should().Be(2);
        harness.Agent.State.PendingWrite.NextRetryAtUtc.Should().BeNull();
        harness.Callbacks.Timeouts.Should().ContainSingle("no retry follows a rejection");
        harness.Alerts.Published.Should().ContainSingle().Which.Stage.Should().Be(TerminalWriteAlertStage);
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
    [InlineData(typeof(ProjectionScopeStatusRouteActivatedEvent), true)]
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
    public void BuildTerminalRoute_ShouldNameTerminalContract()
    {
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(3);

        route.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
        route.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        route.ContractVersion.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);
        route.ContractVersion.Should().Be(ProjectionScopeStatusGAgent.ContractVersion);
        route.RouteEpoch.Should().Be(3);
        route.LegacyRouteReleased.Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route).Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue();
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
    public void IsActiveTerminalRoute_ShouldRequireExactContractAndVersion()
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
            .Should().BeFalse();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                null,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
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
            "both status writers must produce an exact duplicate at the same version, never a conflict");
        first.Id.Should().Be(SourceScopeActorId);
        first.ScopeActorId.Should().Be(SourceScopeActorId);
        first.StateVersion.Should().Be(42);
        first.LastEventId.Should().Be("evt-42");
        first.UpdatedAtUtcValue.Should().Be(Timestamp.FromDateTimeOffset(FixedNow));
        first.LastSuccessfulVersion.Should().Be(41);
        first.SourceVersions.Select(static source => source.SourceActorId).Should().Equal("publisher-a", "publisher-b");
        first.SourceVersions[0].VersionGap.Should().Be(1);
        first.LastSuccessfulSourceCoordinates.Select(static source => source.ActorId)
            .Should().Equal("publisher-a", "publisher-b");
    }

    [Fact]
    public void StatusDocument_ShouldNotCarryStatusRoute()
    {
        // The document is written by two writers during a rolling upgrade (legacy shadow and
        // terminal materializer, possibly on different binaries); its bytes at a given source
        // version must not depend on which writer produced them, so the writer-selecting route
        // stays on the source scope state and is not a document field.
        var descriptor = ProjectionScopeStatusDocument.Descriptor;

        descriptor.FindFieldByName("status_route").Should().BeNull();
        descriptor.FindFieldByNumber(30).Should().BeNull("field 30 is reserved, never reused");
        descriptor.Fields.InDeclarationOrder()
            .Where(static field => field.FieldType == Google.Protobuf.Reflection.FieldType.Message)
            .Should().NotContain(static field => field.MessageType == ProjectionScopeStatusRoute.Descriptor);
        typeof(ProjectionScopeStatusDocument).GetProperty("StatusRoute").Should().BeNull();
    }

    [Fact]
    public void Map_ShouldProduceIdenticalBytesWithAndWithoutStatusRoute()
    {
        var routed = BuildRoutedSourceState();
        routed.LastSuccessfulVersion = 41;
        routed.HighestSeenVersion = 42;
        var unrouted = routed.Clone();
        unrouted.StatusRoute = null;
        var stateEvent = BuildStateEvent(version: 42, eventId: "evt-42", new ProjectionScopeWatermarkAdvancedEvent());

        var fromRouted = ProjectionScopeStatusDocumentMapper.Map(routed, stateEvent.Clone(), FixedNow);
        var fromUnrouted = ProjectionScopeStatusDocumentMapper.Map(unrouted, stateEvent.Clone(), FixedNow);

        fromRouted.ToByteArray().Should().Equal(fromUnrouted.ToByteArray(),
            "the status document bytes must not depend on the source's writer-selecting route");
        fromRouted.Should().Be(fromUnrouted);
    }

    [Fact]
    public void Map_WithDifferentRouteEpochs_ShouldProduceIdenticalBytes()
    {
        var epochOne = BuildRoutedSourceState();
        var epochTwo = BuildRoutedSourceState();
        epochTwo.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(2);
        epochTwo.StatusRoute.LegacyRouteReleased = true;
        var stateEvent = BuildStateEvent(version: 7, eventId: "evt-7", new ProjectionScopeStatusRouteActivatedEvent());

        var first = ProjectionScopeStatusDocumentMapper.Map(epochOne, stateEvent.Clone(), FixedNow);
        var second = ProjectionScopeStatusDocumentMapper.Map(epochTwo, stateEvent.Clone(), FixedNow);

        first.ToByteArray().Should().Equal(second.ToByteArray());
    }

    [Fact]
    public async Task TerminalWrite_ShouldMatchSharedMapperBytes()
    {
        var harness = await TerminalHarness.CreateStartedAsync();
        var sourceState = BuildRoutedSourceState();
        var stateEvent = BuildStateEvent(version: 42, eventId: "evt-42", new ProjectionScopeWatermarkAdvancedEvent());

        await harness.Agent.HandleObservedEnvelopeAsync(BuildForwardedEnvelope(sourceState, stateEvent));

        var expected = ProjectionScopeStatusDocumentMapper.Map(
            sourceState,
            stateEvent,
            stateEvent.Timestamp.ToDateTimeOffset());
        harness.Dispatcher.Documents.Should().ContainSingle()
            .Which.ToByteArray().Should().Equal(expected.ToByteArray());
    }

    // ─── builders ────────────────────────────────────────────────────────────

    private static EnsureProjectionScopeCommand BuildEnsureCommand() =>
        new()
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            Mode = ProjectionScopeMode.DurableMaterialization,
        };

    private static ReleaseProjectionScopeCommand BuildReleaseCommand() =>
        new()
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            Mode = ProjectionScopeMode.DurableMaterialization,
        };

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

        public TerminalHarness(bool withCallbackScheduler = true, bool withAlertSink = true)
        {
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
            _services = services.BuildServiceProvider();
            Agent = CreateReplacementActor();
        }

        public RecordingStreamProvider Streams { get; } = new();
        public RecordingStatusWriteDispatcher Dispatcher { get; } = new();
        public TerminalEventSourcing EventSourcing { get; } = new();
        public RecordingEventPublisher Outbox { get; } = new();
        public RecordingCallbackScheduler Callbacks { get; } = new();
        public RecordingFailureAlertSink Alerts { get; } = new();
        public ProjectionScopeStatusGAgent Agent { get; }

        public static async Task<TerminalHarness> CreateStartedAsync(
            bool withCallbackScheduler = true,
            bool withAlertSink = true)
        {
            var harness = new TerminalHarness(withCallbackScheduler, withAlertSink);
            await harness.Agent.HandleEnsureAsync(BuildEnsureCommand());
            harness.Agent.State.Active.Should().BeTrue();
            return harness;
        }

        /// <summary>
        /// Fires the most recently scheduled durable retry against the current actor: the
        /// scheduler delivers the trigger envelope to the actor's inbox once its due time elapses.
        /// </summary>
        public Task FireLatestRetryAsync() =>
            Agent.HandleRetryWriteAsync(UnpackRetryCommand(Callbacks.Timeouts[^1]));

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

    private sealed class TerminalEventSourcing : IEventSourcingBehavior<ProjectionScopeStatusTerminalState>
    {
        private readonly List<IMessage> _pending = [];
        private ProjectionScopeStatusTerminalState _durableState = new();

        public List<IMessage> PersistedEvents { get; } = [];
        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
                PersistedEvents.Add(evt);
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

    private sealed class RecordingStatusWriteDispatcher : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
    {
        private readonly Queue<object> _outcomes = new();
        private Exception? _alwaysThrow;

        public List<ProjectionScopeStatusDocument> Documents { get; } = [];
        public int UpsertAttempts { get; private set; }

        public void Enqueue(ProjectionWriteResult result) => _outcomes.Enqueue(result);

        public void Enqueue(Exception exception) => _outcomes.Enqueue(exception);

        public void AlwaysThrow(Exception exception) => _alwaysThrow = exception;

        /// <summary>The store is back: writes succeed again (queued outcomes still apply first).</summary>
        public void Recover() => _alwaysThrow = null;

        public Task<ProjectionWriteResult> UpsertAsync(ProjectionScopeStatusDocument readModel, CancellationToken ct = default)
        {
            UpsertAttempts++;
            if (_alwaysThrow != null)
                throw _alwaysThrow;

            if (_outcomes.Count > 0)
            {
                var outcome = _outcomes.Dequeue();
                if (outcome is Exception exception)
                    throw exception;

                Documents.Add(readModel.Clone());
                return Task.FromResult((ProjectionWriteResult)outcome);
            }

            Documents.Add(readModel.Clone());
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Records anything the actor publishes inline. The terminal materializer publishes
    /// nothing this way any more: its only continuation is the durable callback retry.
    /// </summary>
    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<PublishedMessage> Published { get; } = [];

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
            where TEvent : IMessage =>
            throw new NotSupportedException("The terminal materializer never addresses another actor.");
    }

    private sealed record PublishedMessage(IMessage Message, TopologyAudience Audience);

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
