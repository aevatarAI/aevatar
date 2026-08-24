using System.Reflection;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Actor-level tests for the graph-route cutover saga owned by
/// <see cref="WorkflowExecutionMaterializationScopeGAgent"/>:
/// Requested -> CandidateBuilt -> GoldenVerified -> Activated, driven through the
/// actor's own self-addressed <see cref="ContinueProjectionMaterializationCutoverCommand"/>.
/// The scope actor is event-sourced against a real in-memory event store so that every
/// crash/restart boundary replays the committed history exactly as production would.
/// </summary>
public sealed class WorkflowExecutionMaterializationScopeCutoverTests
{
    private const string ScopeActorId = "projection.materialization-scope:workflow-execution-cutover";
    private const string RootActorId = "actor-1";
    private const string RollbackPhysicalNamespace = "workflow-execution-graph.v2.rollback-1";
    private const long InitialReportVersion = 5;
    private const string InitialReportEventId = "evt-5";

    private static readonly DateTimeOffset Now = new(2026, 8, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HappyPath_ShouldDriveRequestedToActivated_ThroughSelfContinuation()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();

        actor.Agent.State.ActiveMaterializationRoute.Should().Be(CompatibilityRoute());
        actor.Agent.State.MaterializationCutover.Should().NotBeNull();
        actor.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Requested);
        actor.Agent.State.MaterializationCutover.CandidateRoute.Should().Be(IncrementalRoute());
        var scheduled = actor.Outbox.Pending.Should().ContainSingle().Subject;
        scheduled.Route.GetTopologyAudience().Should().Be(TopologyAudience.Self);
        scheduled.Payload!.Unpack<ContinueProjectionMaterializationCutoverCommand>()
            .ExpectedRouteEpoch.Should().Be(2);

        var phases = await harness.DrainAsync(actor);

        phases.Should().Equal(
            ProjectionMaterializationCutoverPhase.CandidateBuilt,
            ProjectionMaterializationCutoverPhase.GoldenVerified,
            ProjectionMaterializationCutoverPhase.Activated);
        actor.Outbox.Pending.Should().BeEmpty("activation is terminal and must not reschedule itself");

        var state = actor.Agent.State;
        state.ActiveMaterializationRoute.Should().Be(IncrementalRoute());
        var cutover = state.MaterializationCutover!;
        cutover.Phase.Should().Be(ProjectionMaterializationCutoverPhase.Activated);
        cutover.CandidateRoute.Should().Be(IncrementalRoute());
        cutover.CandidateSource.Should().Be(SourceCoordinate(InitialReportVersion, InitialReportEventId));
        cutover.CandidateFingerprint.Should().Be(harness.ExpectedCandidateFingerprint(IncrementalRoute()));
        cutover.ActivationProof.Should().Be(new ProjectionMaterializationActivationProof
        {
            AuthorityStateVersion = 9,
            CapabilityEpoch = 3,
            MembershipEpoch = 7,
            MembershipDigest = "digest-a",
            DeploymentRevision = "revision-a",
            ValidatedAtUtc = Timestamp.FromDateTimeOffset(Now),
            ValidUntilUtc = Timestamp.FromDateTimeOffset(Now.AddMinutes(1)),
        });

        (await harness.CommittedEventTypesAsync()).Should().Equal(
            ProjectionScopeStartedEvent.Descriptor.FullName,
            ProjectionObservationAttachmentUpdatedEvent.Descriptor.FullName,
            ProjectionMaterializationRouteInitializedEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverRequestedEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverCandidateBuiltEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverGoldenVerifiedEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverActivatedEvent.Descriptor.FullName);

        var snapshot = await harness.ReadCandidateSnapshotAsync(IncrementalRoute());
        snapshot.Disposition.Should().Be(ProjectionGraphOwnerSnapshotReadDisposition.Found);
        snapshot.Snapshot!.Source.Should().Be(new ProjectionGraphSourceCoordinate
        {
            ActorId = RootActorId,
            StateVersion = InitialReportVersion,
            EventId = InitialReportEventId,
        });
    }

    [Fact]
    public async Task GraphDisabled_WhenScopeStarts_ShouldNotRequestOrScheduleCutover()
    {
        var harness = new CutoverScopeHarness(graphProjectionEnabled: false);

        var actor = await harness.StartScopeAsync();

        actor.Agent.State.ActiveMaterializationRoute.Should().Be(CompatibilityRoute());
        actor.Agent.State.MaterializationCutover.Should().BeNull();
        actor.Outbox.Pending.Should().BeEmpty();
        (await harness.CommittedEventTypesAsync()).Should().Equal(
            ProjectionScopeStartedEvent.Descriptor.FullName,
            ProjectionObservationAttachmentUpdatedEvent.Descriptor.FullName,
            ProjectionMaterializationRouteInitializedEvent.Descriptor.FullName);
    }

    [Fact]
    public async Task GraphDisabled_WhenCutoverWasAlreadyRequested_ShouldPauseContinuation()
    {
        var harness = new CutoverScopeHarness();
        var started = await harness.StartScopeAsync();
        started.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Requested);
        var committedBeforeDisable = await harness.CommittedEventTypesAsync();
        var disabledServices = harness.BuildServices(
            adoptionReceiptGranted: true,
            graphProjectionEnabled: false);

        var recovered = await harness.ReactivateScopeAsync(disabledServices);
        await recovered.Agent.HandleContinueMaterializationCutoverAsync(
            new ContinueProjectionMaterializationCutoverCommand { ExpectedRouteEpoch = 2 });

        recovered.Outbox.Pending.Should().BeEmpty();
        recovered.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Requested);
        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBeforeDisable);
    }

    [Theory]
    [InlineData(ProjectionMaterializationCutoverPhase.Requested)]
    [InlineData(ProjectionMaterializationCutoverPhase.CandidateBuilt)]
    [InlineData(ProjectionMaterializationCutoverPhase.GoldenVerified)]
    public async Task Restart_AtPersistedInProgressPhase_ShouldResumeFromThatPhase_WithoutRestartOrDoubleActivation(
        ProjectionMaterializationCutoverPhase crashAfterPhase)
    {
        var harness = new CutoverScopeHarness();
        var crashed = await harness.StartScopeAsync();
        await harness.DrainAsync(crashed, stopAtPhase: crashAfterPhase);
        crashed.Agent.State.MaterializationCutover!.Phase.Should().Be(crashAfterPhase);
        var committedBeforeCrash = await harness.CommittedEventTypesAsync();
        var cutoverBeforeCrash = crashed.Agent.State.MaterializationCutover.Clone();

        // The crashed instance is abandoned mid-flight (its pending self command is lost).
        var recovered = await harness.ReactivateScopeAsync();

        recovered.Agent.State.MaterializationCutover.Should().Be(cutoverBeforeCrash);
        (await harness.CommittedEventTypesAsync()).Should().Equal(
            committedBeforeCrash,
            "re-activation must not append a fresh Requested event or re-initialize the route");
        var continuation = recovered.Outbox.Pending.Should().ContainSingle().Subject;
        continuation.Payload!.Unpack<ContinueProjectionMaterializationCutoverCommand>()
            .ExpectedRouteEpoch.Should().Be(2);

        var phases = await harness.DrainAsync(recovered);

        phases.Should().Equal(RemainingPhasesAfter(crashAfterPhase));
        recovered.Agent.State.ActiveMaterializationRoute.Should().Be(IncrementalRoute());
        var committedAfterRecovery = await harness.CommittedEventTypesAsync();
        committedAfterRecovery.Count(static type =>
                type == ProjectionMaterializationCutoverRequestedEvent.Descriptor.FullName)
            .Should().Be(1);
        committedAfterRecovery.Count(static type =>
                type == ProjectionMaterializationCutoverActivatedEvent.Descriptor.FullName)
            .Should().Be(1);
        committedAfterRecovery.Should().HaveCount(7);
    }

    [Fact]
    public async Task Restart_AfterActivation_ShouldNotScheduleContinuation_OrAppendEvents()
    {
        var harness = new CutoverScopeHarness();
        var activated = await harness.StartScopeAsync();
        await harness.DrainAsync(activated);
        activated.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Activated);
        var committedBeforeRestart = await harness.CommittedEventTypesAsync();

        var restarted = await harness.ReactivateScopeAsync();

        restarted.Outbox.Pending.Should().BeEmpty();
        restarted.Agent.State.ActiveMaterializationRoute.Should().Be(IncrementalRoute());
        restarted.Agent.State.MaterializationCutover.Should().Be(activated.Agent.State.MaterializationCutover);
        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBeforeRestart);
    }

    [Fact]
    public async Task ReportAdvancesAfterCandidateBuilt_ShouldRestartAtRequested_AndRebuildAgainstNewGolden()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        await harness.DrainAsync(actor, stopAtPhase: ProjectionMaterializationCutoverPhase.CandidateBuilt);
        actor.Agent.State.MaterializationCutover!.CandidateSource.Should()
            .Be(SourceCoordinate(InitialReportVersion, InitialReportEventId));
        var staleFingerprint = actor.Agent.State.MaterializationCutover.CandidateFingerprint;

        harness.AdvanceReport(6, "evt-6", new WorkflowExecutionStepTrace
        {
            StepId = "step-2",
            StepType = "assign",
        });

        var phases = await harness.DrainAsync(actor);

        phases.Should().Equal(
            ProjectionMaterializationCutoverPhase.Requested,
            ProjectionMaterializationCutoverPhase.CandidateBuilt,
            ProjectionMaterializationCutoverPhase.GoldenVerified,
            ProjectionMaterializationCutoverPhase.Activated);
        var cutover = actor.Agent.State.MaterializationCutover!;
        cutover.CandidateSource.Should().Be(SourceCoordinate(6, "evt-6"));
        cutover.CandidateFingerprint.Should().NotBe(staleFingerprint);
        cutover.CandidateFingerprint.Should().Be(harness.ExpectedCandidateFingerprint(IncrementalRoute()));
        actor.Agent.State.ActiveMaterializationRoute.Should().Be(IncrementalRoute());
        (await harness.CommittedEventTypesAsync()).Should().Equal(
            ProjectionScopeStartedEvent.Descriptor.FullName,
            ProjectionObservationAttachmentUpdatedEvent.Descriptor.FullName,
            ProjectionMaterializationRouteInitializedEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverRequestedEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverCandidateBuiltEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverRequestedEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverCandidateBuiltEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverGoldenVerifiedEvent.Descriptor.FullName,
            ProjectionMaterializationCutoverActivatedEvent.Descriptor.FullName);
        var snapshot = await harness.ReadCandidateSnapshotAsync(IncrementalRoute());
        snapshot.Snapshot!.Source.StateVersion.Should().Be(6);
        snapshot.Snapshot.Source.EventId.Should().Be("evt-6");
        snapshot.Snapshot.Nodes.Should().Contain(node =>
            node.NodeId.EndsWith(":step-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FleetAdmissionNotGrantedAtFlip_ShouldStayGoldenVerified_ThenActivateOnceGranted()
    {
        var harness = new CutoverScopeHarness(fleetAdmissionGranted: false);
        var actor = await harness.StartScopeAsync();

        var phases = await harness.DrainAsync(actor);

        phases.Should().Equal(
            ProjectionMaterializationCutoverPhase.CandidateBuilt,
            ProjectionMaterializationCutoverPhase.GoldenVerified,
            ProjectionMaterializationCutoverPhase.GoldenVerified);
        actor.Outbox.Pending.Should().BeEmpty("a denied flip must not spin the continuation loop");
        actor.Agent.State.ActiveMaterializationRoute.Should().Be(CompatibilityRoute());
        actor.Agent.State.MaterializationCutover!.ActivationProof.Should().BeNull();
        (await harness.CommittedEventTypesAsync()).Should().NotContain(
            ProjectionMaterializationCutoverActivatedEvent.Descriptor.FullName);

        harness.GrantFleetAdmission();
        // A materialized observation re-arms the in-progress saga; the flip then succeeds.
        await actor.Agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservation(7, "evt-7"));

        harness.Materializer.Routes.Should().ContainSingle().Which.Should().Be(CompatibilityRoute());
        actor.Outbox.Pending.Should().ContainSingle();
        var resumed = await harness.DrainAsync(actor);
        resumed.Should().Equal(ProjectionMaterializationCutoverPhase.Activated);
        actor.Agent.State.ActiveMaterializationRoute.Should().Be(IncrementalRoute());
        actor.Agent.State.MaterializationCutover!.ActivationProof.Should().NotBeNull();
    }

    [Fact]
    public async Task ContinueCommand_WithStaleExpectedRouteEpoch_ShouldBeIgnored()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        var committedBefore = await harness.CommittedEventTypesAsync();
        var pendingBefore = actor.Outbox.Pending.Count;

        await actor.Agent.HandleContinueMaterializationCutoverAsync(
            new ContinueProjectionMaterializationCutoverCommand { ExpectedRouteEpoch = 1 });
        await actor.Agent.HandleContinueMaterializationCutoverAsync(
            new ContinueProjectionMaterializationCutoverCommand { ExpectedRouteEpoch = 3 });

        actor.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Requested);
        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBefore);
        actor.Outbox.Pending.Should().HaveCount(pendingBefore);
        (await harness.ReadCandidateSnapshotAsync(IncrementalRoute())).Disposition.Should()
            .Be(ProjectionGraphOwnerSnapshotReadDisposition.NotFound, "no candidate may be built for a stale epoch");
    }

    [Fact]
    public async Task RequestCutover_WhenExpectedActiveRouteEpochMismatches_ShouldBeIgnored()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        await harness.DrainAsync(actor);
        var committedBefore = await harness.CommittedEventTypesAsync();

        await actor.Agent.HandleRequestMaterializationCutoverAsync(
            new RequestProjectionMaterializationCutoverCommand
            {
                ExpectedActiveRouteEpoch = 1,
                CandidateRoute = IncrementalRoute(routeEpoch: 3, physicalNamespace: RollbackPhysicalNamespace),
            });

        actor.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Activated);
        actor.Agent.State.MaterializationCutover.CandidateRoute.Should().Be(IncrementalRoute());
        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBefore);
        actor.Outbox.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestCutover_WhenCandidateDoesNotAdvanceEpochByExactlyOne_ShouldThrow()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        await harness.DrainAsync(actor);
        var committedBefore = await harness.CommittedEventTypesAsync();

        var act = () => actor.Agent.HandleRequestMaterializationCutoverAsync(
            new RequestProjectionMaterializationCutoverCommand
            {
                ExpectedActiveRouteEpoch = 2,
                CandidateRoute = IncrementalRoute(routeEpoch: 4, physicalNamespace: RollbackPhysicalNamespace),
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*advance the route epoch by exactly one*");
        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBefore);
        actor.Outbox.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestCutover_WhenCandidateReusesActivePhysicalNamespace_ShouldThrow()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        await harness.DrainAsync(actor);
        var committedBefore = await harness.CommittedEventTypesAsync();

        var act = () => actor.Agent.HandleRequestMaterializationCutoverAsync(
            new RequestProjectionMaterializationCutoverCommand
            {
                ExpectedActiveRouteEpoch = 2,
                CandidateRoute = IncrementalRoute(routeEpoch: 3),
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different physical namespace*");
        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBefore);
        actor.Outbox.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestCutover_WhileAnotherCutoverIsInProgress_WithDifferentCandidate_ShouldThrow()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        var committedBefore = await harness.CommittedEventTypesAsync();
        var pendingBefore = actor.Outbox.Pending.Count;

        var act = () => actor.Agent.HandleRequestMaterializationCutoverAsync(
            new RequestProjectionMaterializationCutoverCommand
            {
                ExpectedActiveRouteEpoch = 1,
                CandidateRoute = IncrementalRoute(routeEpoch: 2, physicalNamespace: RollbackPhysicalNamespace),
            });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Another materialization route cutover is already in progress.");
        actor.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Requested);
        actor.Agent.State.MaterializationCutover.CandidateRoute.Should().Be(IncrementalRoute());
        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBefore);
        actor.Outbox.Pending.Should().HaveCount(pendingBefore);
    }

    [Fact]
    public async Task RequestCutover_WithSameInProgressCandidate_ShouldOnlyRescheduleContinuation()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        var committedBefore = await harness.CommittedEventTypesAsync();
        actor.Outbox.Pending.Clear();

        await actor.Agent.HandleRequestMaterializationCutoverAsync(
            new RequestProjectionMaterializationCutoverCommand
            {
                ExpectedActiveRouteEpoch = 1,
                CandidateRoute = IncrementalRoute(),
            });

        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBefore);
        var rescheduled = actor.Outbox.Pending.Should().ContainSingle().Subject;
        rescheduled.Payload!.Unpack<ContinueProjectionMaterializationCutoverCommand>()
            .ExpectedRouteEpoch.Should().Be(2);
    }

    [Fact]
    public async Task RequestCutover_WithValidRollbackCandidate_ShouldRunSagaAndActivateNextEpoch()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        await harness.DrainAsync(actor);
        var rollbackRoute = IncrementalRoute(routeEpoch: 3, physicalNamespace: RollbackPhysicalNamespace);

        await actor.Agent.HandleRequestMaterializationCutoverAsync(
            new RequestProjectionMaterializationCutoverCommand
            {
                ExpectedActiveRouteEpoch = 2,
                CandidateRoute = rollbackRoute,
            });

        actor.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Requested);
        actor.Agent.State.MaterializationCutover.CandidateRoute.Should().Be(rollbackRoute);
        actor.Agent.State.ActiveMaterializationRoute.Should().Be(IncrementalRoute(), "the previous route stays active until the flip");
        var phases = await harness.DrainAsync(actor);
        phases.Should().Equal(
            ProjectionMaterializationCutoverPhase.CandidateBuilt,
            ProjectionMaterializationCutoverPhase.GoldenVerified,
            ProjectionMaterializationCutoverPhase.Activated);
        actor.Agent.State.ActiveMaterializationRoute.Should().Be(rollbackRoute);
        actor.Agent.State.MaterializationCutover!.CandidateFingerprint.Should()
            .Be(harness.ExpectedCandidateFingerprint(rollbackRoute));
        (await harness.ReadCandidateSnapshotAsync(rollbackRoute)).Disposition.Should()
            .Be(ProjectionGraphOwnerSnapshotReadDisposition.Found);
    }

    [Fact]
    public async Task WithoutAdoptionReceipt_ShouldNotRequestCutover_AndObserveOnCompatibilityRoute()
    {
        var harness = new CutoverScopeHarness(adoptionReceiptGranted: false);
        var actor = await harness.StartScopeAsync();

        actor.Agent.State.ActiveMaterializationRoute.Should().Be(CompatibilityRoute());
        actor.Agent.State.MaterializationCutover.Should().BeNull();
        actor.Outbox.Pending.Should().BeEmpty();
        (await harness.CommittedEventTypesAsync()).Should().Equal(
            ProjectionScopeStartedEvent.Descriptor.FullName,
            ProjectionObservationAttachmentUpdatedEvent.Descriptor.FullName,
            ProjectionMaterializationRouteInitializedEvent.Descriptor.FullName);

        await actor.Agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservation(7, "evt-7"));

        harness.Materializer.Routes.Should().ContainSingle().Which.Should().Be(CompatibilityRoute());
        actor.Agent.State.MaterializationCutover.Should().BeNull();
        actor.Outbox.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAdoptionReceipt_WhenActiveRouteIsIncremental_ShouldFailClosed()
    {
        var harness = new CutoverScopeHarness();
        var activated = await harness.StartScopeAsync();
        await harness.DrainAsync(activated);
        activated.Agent.State.ActiveMaterializationRoute.Should().Be(IncrementalRoute());

        // Same committed history, but the runtime no longer carries the exact adoption receipt.
        var withoutReceipt = await harness.ReactivateScopeAsync(
            harness.BuildServices(adoptionReceiptGranted: false));
        withoutReceipt.Agent.State.ActiveMaterializationRoute.Should().Be(IncrementalRoute());
        withoutReceipt.Outbox.Pending.Should().BeEmpty();

        var prepare = typeof(WorkflowExecutionMaterializationScopeGAgent).GetMethod(
            "PrepareObservationContextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var context = new WorkflowExecutionMaterializationContext
        {
            RootActorId = RootActorId,
            ProjectionKind = WorkflowProjectionKinds.ExecutionMaterialization,
        };
        var act = () => prepare.Invoke(
            withoutReceipt.Agent,
            [context, BuildForwardedCommittedObservation(7, "evt-7"), CancellationToken.None]);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("The incremental workflow graph route requires the exact adopted scope schema receipt.");
        context.MaterializationRoute.Should().BeNull();

        await withoutReceipt.Agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservation(7, "evt-7"));
        harness.Materializer.Routes.Should().BeEmpty("no materializer may run on an unadmitted incremental route");
        withoutReceipt.Agent.State.LastSuccessfulSourceCoordinatesByActor.Should().NotContainKey(RootActorId);
    }

    [Fact]
    public async Task AfterActivation_ObservedCommittedEnvelope_ShouldMaterializeOnIncrementalRoute()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        await harness.DrainAsync(actor);
        var committedBefore = await harness.CommittedEventTypesAsync();

        await actor.Agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservation(7, "evt-7"));

        harness.Materializer.Routes.Should().ContainSingle().Which.Should().Be(IncrementalRoute());
        harness.Materializer.Contexts.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            RootActorId,
            ProjectionKind = WorkflowProjectionKinds.ExecutionMaterialization,
        });
        actor.Agent.State.LastSuccessfulSourceCoordinatesByActor[RootActorId]
            .Should().Be(SourceCoordinate(7, "evt-7"));
        actor.Agent.State.InFlightObservation.Should().BeNull();
        actor.Outbox.Pending.Should().BeEmpty("an activated cutover must not be re-armed by observations");
        (await harness.CommittedEventTypesAsync()).Should().StartWith(committedBefore);
    }

    [Fact]
    public async Task OverBoundCandidate_ShouldAbortCutoverAndStayOnCompatibilityRoute()
    {
        var harness = new CutoverScopeHarness(maximumCandidateMutationCount: 1);
        var actor = await harness.StartScopeAsync();
        actor.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Requested);

        var phases = await harness.DrainAsync(actor);

        phases.Should().Equal(ProjectionMaterializationCutoverPhase.Aborted);
        actor.Outbox.Pending.Should().BeEmpty("an aborted cutover must not reschedule itself");
        var state = actor.Agent.State;
        state.ActiveMaterializationRoute.Should().Be(CompatibilityRoute(),
            "an over-bound owner stays on the compatibility route");
        state.MaterializationCutover!.Phase.Should().Be(ProjectionMaterializationCutoverPhase.Aborted);
        state.MaterializationCutover.AbortReason.Should().Contain("mutations");

        // Observations keep flowing on the compatibility route and never re-arm the saga.
        await actor.Agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservation(7, "evt-7"));
        harness.Materializer.Routes.Should().ContainSingle().Which.Should().Be(CompatibilityRoute());
        actor.Outbox.Pending.Should().BeEmpty();
        actor.Agent.State.LastSuccessfulSourceCoordinatesByActor[RootActorId]
            .Should().Be(SourceCoordinate(7, "evt-7"));

        // A restart replays the aborted saga and does not request a fresh cutover.
        var committedBefore = await harness.CommittedEventTypesAsync();
        var restarted = await harness.ReactivateScopeAsync();
        restarted.Outbox.Pending.Should().BeEmpty();
        restarted.Agent.State.MaterializationCutover!.Phase.Should()
            .Be(ProjectionMaterializationCutoverPhase.Aborted);
        (await harness.CommittedEventTypesAsync()).Should().Equal(committedBefore);
    }

    [Fact]
    public async Task OverBoundReplacement_OnIncrementalRoute_ShouldRollBackRouteAndRetryOnCompatibilityRoute()
    {
        var harness = new CutoverScopeHarness();
        var actor = await harness.StartScopeAsync();
        await harness.DrainAsync(actor);
        actor.Agent.State.ActiveMaterializationRoute.Should().Be(IncrementalRoute());
        var committedBefore = await harness.CommittedEventTypesAsync();
        harness.Materializer.ThrowOverBoundOnIncrementalRoute = true;

        await actor.Agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservation(7, "evt-7"));

        var state = actor.Agent.State;
        state.ActiveMaterializationRoute.Should().Be(CompatibilityRoute(routeEpoch: 3),
            "the route rolls back to the compatibility writer at an advanced epoch");
        state.MaterializationCutover!.Phase.Should().Be(ProjectionMaterializationCutoverPhase.Aborted);
        state.MaterializationCutover.AbortReason.Should().Contain("mutations");
        harness.Materializer.Routes.Should().HaveCount(2);
        harness.Materializer.Routes[0].Should().Be(IncrementalRoute());
        harness.Materializer.Routes[1].Should().Be(CompatibilityRoute(routeEpoch: 3));
        state.LastSuccessfulSourceCoordinatesByActor[RootActorId].Should().Be(SourceCoordinate(7, "evt-7"));
        state.InFlightObservation.Should().BeNull();
        (await harness.CommittedEventTypesAsync()).Should().Equal(
            [
                .. committedBefore,
                ProjectionScopeEnvelopeReceivedEvent.Descriptor.FullName,
                ProjectionScopeEnvelopeAttemptedEvent.Descriptor.FullName,
                ProjectionScopeObservationStagedEvent.Descriptor.FullName,
                ProjectionMaterializationRouteRolledBackEvent.Descriptor.FullName,
                ProjectionScopeWatermarkAdvancedEvent.Descriptor.FullName,
            ],
            "the rollback must be committed before the recovered observation advances its watermark");

        // Later observations run on the compatibility route without re-arming the saga.
        harness.Materializer.ThrowOverBoundOnIncrementalRoute = false;
        await actor.Agent.HandleObservedEnvelopeAsync(BuildForwardedCommittedObservation(8, "evt-8"));
        harness.Materializer.Routes.Should().HaveCount(3);
        harness.Materializer.Routes[2].Should().Be(CompatibilityRoute(routeEpoch: 3));
        actor.Outbox.Pending.Should().BeEmpty();

        // A restart replays the rollback; the route stays on the compatibility writer.
        var restarted = await harness.ReactivateScopeAsync();
        restarted.Agent.State.ActiveMaterializationRoute.Should().Be(CompatibilityRoute(routeEpoch: 3));
        restarted.Outbox.Pending.Should().BeEmpty();
    }

    private static IEnumerable<ProjectionMaterializationCutoverPhase> RemainingPhasesAfter(
        ProjectionMaterializationCutoverPhase phase) =>
        phase switch
        {
            ProjectionMaterializationCutoverPhase.Requested =>
            [
                ProjectionMaterializationCutoverPhase.CandidateBuilt,
                ProjectionMaterializationCutoverPhase.GoldenVerified,
                ProjectionMaterializationCutoverPhase.Activated,
            ],
            ProjectionMaterializationCutoverPhase.CandidateBuilt =>
            [
                ProjectionMaterializationCutoverPhase.GoldenVerified,
                ProjectionMaterializationCutoverPhase.Activated,
            ],
            ProjectionMaterializationCutoverPhase.GoldenVerified =>
            [
                ProjectionMaterializationCutoverPhase.Activated,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
        };

    private static ProjectionMaterializationRouteFingerprint CompatibilityRoute(long routeEpoch = 1) =>
        new()
        {
            ContractId = WorkflowExecutionGraphConstants.LegacyContractId,
            ContractVersion = WorkflowExecutionGraphConstants.LegacyContractVersion,
            PhysicalNamespace = WorkflowExecutionGraphConstants.Scope,
            RouteEpoch = routeEpoch,
        };

    private static ProjectionMaterializationRouteFingerprint IncrementalRoute(
        long routeEpoch = 2,
        string physicalNamespace = WorkflowExecutionGraphConstants.IncrementalPhysicalNamespace) =>
        new()
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            ContractVersion = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            PhysicalNamespace = physicalNamespace,
            RouteEpoch = routeEpoch,
        };

    private static ProjectionSourceCoordinate SourceCoordinate(long stateVersion, string eventId) =>
        new()
        {
            ActorId = RootActorId,
            StateVersion = stateVersion,
            EventId = eventId,
        };

    private static EventEnvelope BuildForwardedCommittedObservation(long version, string eventId)
    {
        var original = new EventEnvelope
        {
            Id = $"outer-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(Now),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = RootActorId,
                    EventId = eventId,
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(Now),
                    EventType = StepRequestEvent.Descriptor.FullName,
                    EventData = Any.Pack(new StepRequestEvent
                    {
                        RunId = "run-1",
                        StepId = "step-2",
                        StepType = "assign",
                    }),
                },
                StateRoot = Any.Pack(new WorkflowRunState
                {
                    RunId = "run-1",
                    WorkflowName = "workflow",
                    LastCommandId = "cmd-1",
                    Status = "running",
                }),
            }),
        };

        return StreamForwardingRules.BuildForwardedEnvelope(
            original,
            sourceStreamId: RootActorId,
            targetStreamId: ScopeActorId,
            StreamForwardingMode.HandleThenForward);
    }

    private sealed record ScopeActor(
        WorkflowExecutionMaterializationScopeGAgent Agent,
        SelfCommandOutbox Outbox);

    /// <summary>
    /// One scope actor's world: a shared in-memory committed history, the authoritative
    /// report, the versioned graph store, fleet admission evidence and the recorded
    /// materializer. Actor instances are disposable; the world survives restarts.
    /// </summary>
    private sealed class CutoverScopeHarness
    {
        private readonly InMemoryEventStore _eventStore = new();
        private readonly InMemoryProjectionGraphStore _graphStore = new();
        private readonly InMemoryStreamProvider _streamProvider = new();
        private readonly MutableReportReader _reportReader;
        private readonly MutableFleetAdmissionSource _fleet;
        private readonly WorkflowRunIncrementalGraphMaterializer _graphMaterializer;
        private readonly IServiceProvider _services;

        public CutoverScopeHarness(
            bool adoptionReceiptGranted = true,
            bool fleetAdmissionGranted = true,
            int? maximumCandidateMutationCount = null,
            bool graphProjectionEnabled = true)
        {
            _graphMaterializer = new WorkflowRunIncrementalGraphMaterializer(
                ProjectionGraphOwnerIdentityResolver.Instance,
                maximumCandidateMutationCount ??
                WorkflowRunIncrementalGraphMaterializer.MaximumCandidateMutationCount);
            _reportReader = new MutableReportReader(BuildReport());
            _fleet = new MutableFleetAdmissionSource(fleetAdmissionGranted ? CreateAdmission() : null);
            _services = BuildServices(adoptionReceiptGranted, graphProjectionEnabled);
        }

        public RecordingMaterializer Materializer { get; } = new();

        public IServiceProvider BuildServices(
            bool adoptionReceiptGranted,
            bool graphProjectionEnabled = true)
        {
            var services = new ServiceCollection()
                .AddSingleton<IEventStore>(_eventStore)
                .AddSingleton(new EventSourcingRuntimeOptions())
                .AddSingleton<IStreamProvider>(_streamProvider)
                .AddSingleton<IActorRuntimeCallbackScheduler, UnsupportedCallbackScheduler>()
                .AddSingleton<TimeProvider>(new FixedTimeProvider(Now))
                .AddSingleton<IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>>(_reportReader)
                .AddSingleton<IVersionedProjectionGraphStore>(_graphStore)
                .AddSingleton(new ProjectionGraphProviderStatus(
                    graphProjectionEnabled ? "InMemory" : "Disabled",
                    graphProjectionEnabled))
                .AddSingleton(_graphMaterializer)
                .AddSingleton<WorkflowProjectionGraphCutoverOrchestrator>()
                .AddSingleton<IRuntimeFleetCapabilityAdmissionReader>(_fleet)
                .AddSingleton<IRuntimeLocalMembershipIdentityReader>(_fleet)
                .AddSingleton<IProjectionMaterializer<WorkflowExecutionMaterializationContext>>(Materializer)
                .AddTransient(
                    typeof(IEventSourcingBehaviorFactory<>),
                    typeof(DefaultEventSourcingBehaviorFactory<>));
            if (adoptionReceiptGranted)
            {
                services.AddSingleton<IRuntimeActorStateSchemaContextReader>(
                    new FixedSchemaContextReader(new RuntimeActorStateSchemaContext(
                        WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                        WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion,
                        [CreateGraphAdoptionReceipt(), CreateActivationSealAdoptionReceipt()])));
            }

            // Same registration the production module performs: the scope kind is registered
            // together with its typed v0 -> v1 state migration chain.
            services.AddAevatarAgentKindRegistry(builder =>
                builder.ScanAssemblies(typeof(WorkflowExecutionMaterializationScopeGAgent).Assembly));
            services.AddProjectionMaterializationRuntimeCore<
                WorkflowExecutionMaterializationContext,
                WorkflowExecutionMaterializationRuntimeLease,
                WorkflowExecutionMaterializationScopeGAgent>(
                scopeKey => new WorkflowExecutionMaterializationContext
                {
                    RootActorId = scopeKey.RootActorId,
                    ProjectionKind = scopeKey.ProjectionKind,
                },
                context => new WorkflowExecutionMaterializationRuntimeLease(context));
            return services.BuildServiceProvider();
        }

        /// <summary>Creates a fresh scope actor, activates it, and ensures the durable scope.</summary>
        public async Task<ScopeActor> StartScopeAsync()
        {
            var actor = CreateActor(_services);
            await actor.Agent.ActivateAsync();
            await actor.Agent.HandleEnsureAsync(new EnsureProjectionScopeCommand
            {
                RootActorId = RootActorId,
                ProjectionKind = WorkflowProjectionKinds.ExecutionMaterialization,
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
            return actor;
        }

        /// <summary>Creates a fresh actor instance over the same committed history and activates it.</summary>
        public async Task<ScopeActor> ReactivateScopeAsync(IServiceProvider? services = null)
        {
            var actor = CreateActor(services ?? _services);
            await actor.Agent.ActivateAsync();
            return actor;
        }

        /// <summary>
        /// Delivers the actor's self-addressed continuation commands in publication order until
        /// the outbox is empty (or the requested phase is reached), returning the phase observed
        /// after each delivered command.
        /// </summary>
        public async Task<IReadOnlyList<ProjectionMaterializationCutoverPhase>> DrainAsync(
            ScopeActor actor,
            ProjectionMaterializationCutoverPhase? stopAtPhase = null)
        {
            var phases = new List<ProjectionMaterializationCutoverPhase>();
            var delivered = 0;
            while (actor.Outbox.Pending.Count > 0)
            {
                if (stopAtPhase.HasValue &&
                    actor.Agent.State.MaterializationCutover?.Phase == stopAtPhase.Value)
                {
                    break;
                }

                if (++delivered > 16)
                    throw new InvalidOperationException("The cutover continuation loop did not converge.");

                await actor.Agent.HandleEventAsync(actor.Outbox.Pending.Dequeue());
                phases.Add(actor.Agent.State.MaterializationCutover?.Phase
                           ?? ProjectionMaterializationCutoverPhase.Unspecified);
            }

            return phases;
        }

        public void AdvanceReport(long stateVersion, string eventId, WorkflowExecutionStepTrace step)
        {
            var report = _reportReader.Document;
            report.StateVersion = stateVersion;
            report.LastEventId = eventId;
            report.UpdatedAt = report.UpdatedAt.AddSeconds(1);
            AddStep(report, step);
        }

        public void GrantFleetAdmission() => _fleet.Admission = CreateAdmission();

        public string ExpectedCandidateFingerprint(ProjectionMaterializationRouteFingerprint route) =>
            _graphMaterializer.ComputeExpectedCandidateFingerprint(
                _reportReader.Document,
                WorkflowProjectionKinds.ExecutionMaterialization,
                route);

        public Task<ProjectionGraphOwnerSnapshotReadResult> ReadCandidateSnapshotAsync(
            ProjectionMaterializationRouteFingerprint route) =>
            _graphStore.ReadOwnerSnapshotAsync(_graphMaterializer.ResolveStoreRoute(
                WorkflowProjectionKinds.ExecutionMaterialization,
                _reportReader.Document.Id,
                route));

        public async Task<IReadOnlyList<string>> CommittedEventTypesAsync()
        {
            var events = await _eventStore.GetEventsAsync(ScopeActorId);
            return events.Select(static evt => evt.EventType).ToArray();
        }

        private static ScopeActor CreateActor(IServiceProvider services)
        {
            var outbox = new SelfCommandOutbox(ScopeActorId);
            var agent = new WorkflowExecutionMaterializationScopeGAgent
            {
                Services = services,
                EventPublisher = outbox,
                EventSourcingBehaviorFactory = services.GetRequiredService<
                    IEventSourcingBehaviorFactory<ProjectionScopeState>>(),
            };
            typeof(GAgentBase)
                .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(agent, ScopeActorId);
            return new ScopeActor(agent, outbox);
        }

        private static WorkflowRunInsightReportDocument BuildReport()
        {
            var report = new WorkflowRunInsightReportDocument
            {
                Id = RootActorId,
                RootActorId = RootActorId,
                CommandId = "cmd-1",
                WorkflowName = "workflow",
                StateVersion = InitialReportVersion,
                LastEventId = InitialReportEventId,
                UpdatedAt = Now,
            };
            AddStep(report, new WorkflowExecutionStepTrace
            {
                StepId = "step-1",
                StepType = "assign",
            });
            return report;
        }

        private static void AddStep(
            WorkflowRunInsightReportDocument report,
            WorkflowExecutionStepTrace step)
        {
            report.StepIndexById[step.StepId] = report.Steps.Count;
            report.Steps.Add(step);
        }

        private static RuntimeFleetCapabilityAdmission CreateAdmission()
        {
            var admission = new RuntimeFleetCapabilityAdmission
            {
                Capability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                Status = RuntimeFleetCapabilityGateStatus.Open,
                AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                AuthorityStateVersion = 9,
                CapabilityEpoch = 3,
                MembershipEpoch = 7,
                DeploymentRevision = "revision-a",
                MinimumReaderContractVersion =
                    RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
                MembershipObservedAt = Timestamp.FromDateTimeOffset(Now.AddSeconds(-5)),
                MembershipValidUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(1)),
                ActiveMemberCount = 1,
                ConfirmedMemberCount = 1,
                MembershipDigest = "digest-a",
                ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            };
            admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember
            {
                MemberId = "member-a",
                Incarnation = "inc-a",
            });
            return admission;
        }

        private static RuntimeActorStateSchemaAdoptionReceipt CreateGraphAdoptionReceipt() =>
            new()
            {
                StateSchemaVersion =
                    WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion,
                RequiredCapability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                RequiredContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
                RequiredContractVersion =
                    RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
                CapabilityEpoch = 3,
                AuthorityStateVersion = 9,
                MembershipEpoch = 7,
                DeploymentRevision = "revision-a",
                AdoptedAt = Timestamp.FromDateTimeOffset(Now),
                AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                MembershipDigest = "digest-a",
                EvidenceStatus = RuntimeFleetCapabilityGateStatus.Open,
            };

        private static RuntimeActorStateSchemaAdoptionReceipt CreateActivationSealAdoptionReceipt() =>
            new()
            {
                StateSchemaVersion = WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion,
                RequiredCapability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
                RequiredContractId =
                    RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
                RequiredContractVersion =
                    RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
                CapabilityEpoch = 4,
                AuthorityStateVersion = 10,
                MembershipEpoch = 7,
                DeploymentRevision = "revision-a",
                AdoptedAt = Timestamp.FromDateTimeOffset(Now),
                AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                MembershipDigest = "digest-a",
                EvidenceStatus = RuntimeFleetCapabilityGateStatus.Open,
            };
    }

    /// <summary>Captures the actor's self-addressed commands as an ordered, replayable inbox.</summary>
    private sealed class SelfCommandOutbox(string actorId) : IEventPublisher
    {
        public Queue<EventEnvelope> Pending { get; } = new();

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            Pending.Enqueue(new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, audience),
                Payload = Any.Pack(evt),
            });
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            throw new NotSupportedException();
    }

    private sealed class RecordingMaterializer : IProjectionMaterializer<WorkflowExecutionMaterializationContext>
    {
        public List<ProjectionMaterializationRouteFingerprint?> Routes { get; } = [];
        public List<WorkflowExecutionMaterializationContext> Contexts { get; } = [];
        public bool ThrowOverBoundOnIncrementalRoute { get; set; }

        public ValueTask ProjectAsync(
            WorkflowExecutionMaterializationContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Contexts.Add(context);
            Routes.Add(context.MaterializationRoute?.Clone());
            if (ThrowOverBoundOnIncrementalRoute &&
                WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(context.MaterializationRoute))
            {
                throw new ProjectionGraphCandidateOverBoundException(
                    WorkflowRunIncrementalGraphMaterializer.MaximumCandidateMutationCount + 1,
                    WorkflowRunIncrementalGraphMaterializer.MaximumCandidateMutationCount);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableReportReader(WorkflowRunInsightReportDocument document)
        : IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>
    {
        public WorkflowRunInsightReportDocument Document { get; } = document;

        public Task<WorkflowRunInsightReportDocument?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowRunInsightReportDocument?>(
                string.Equals(key, Document.Id, StringComparison.Ordinal) ? Document.Clone() : null);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowRunInsightReportDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableFleetAdmissionSource(RuntimeFleetCapabilityAdmission? admission)
        : IRuntimeFleetCapabilityAdmissionReader, IRuntimeLocalMembershipIdentityReader
    {
        public RuntimeFleetCapabilityAdmission? Admission { get; set; } = admission;

        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                capability == RuntimeFleetCapability.ProjectionIncrementalGraphV1 ? Admission?.Clone() : null);
        }

        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(
                new RuntimeLocalMembershipIdentity(7, "digest-a", "revision-a", "member-a", "inc-a"));
        }
    }

    private sealed class FixedSchemaContextReader(RuntimeActorStateSchemaContext current)
        : IRuntimeActorStateSchemaContextReader
    {
        public RuntimeActorStateSchemaContext? Current { get; } = current;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The cutover saga never schedules runtime callbacks; any attempt is a test failure.</summary>
    private sealed class UnsupportedCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
