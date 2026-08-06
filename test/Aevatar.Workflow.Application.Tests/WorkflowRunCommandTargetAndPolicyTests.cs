using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Reporting;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Reporting;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Application.Tests;

// Test-add (test-coverage/cluster-036):
//   Covers refactor-introduced behavior in WorkflowRunCommandTarget.cs:116-122,286-290,293-309.
//   Cluster intent: workflow target owns detached durable fallback and cleanup decisions.
// Test-add (test-coverage/cluster-035):
//   Covers refactor-introduced behavior in WorkflowRunCommandTarget.cs:76-189.
//   Cluster intent: workflow run targets detach explicit live-sink leases without process-local lookup state.
public sealed class WorkflowRunCommandTargetAndPolicyTests
{
    [Fact]
    public void RequireLiveSink_ShouldThrow_WhenLiveObservationNotBound()
    {
        var target = CreateTarget();

        var act = () => target.RequireLiveSink();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*live sink is not bound*");
    }

    [Fact]
    public void Constructor_ShouldRejectMissingDurableCompletionResolver()
    {
        var projectionPort = new FakeProjectionPort();
        var act = () => new WorkflowRunCommandTarget("run-1",
            "direct",
            [],
            projectionPort,
            new FakeWorkflowRunActorPort(),
            durableCompletionResolver: null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("durableCompletionResolver");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldDetachReleaseDisposeAndDestroyCreatedActors()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            createdActorIds: ["definition-1", "run-1"]);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());
        var detached = false;

        await target.ReleaseAsync(
            onDetachedAsync: () =>
            {
                detached = true;
                return Task.CompletedTask;
            },
            destroyCreatedActors: true,
            ct: CancellationToken.None);

        detached.Should().BeTrue();
        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        actorPort.DestroyCalls.Should().Equal("run-1", "definition-1");
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_WhenNonTerminal_ShouldReleaseProjectionWithoutDestroyingActors()
    {
        var projectionPort = new FakeProjectionPort();
        var target = CreateTarget(
            projectionPort: projectionPort,
            createdActorIds: ["definition-1", "run-1"]);
        var lease = new FakeProjectionLease("run-1", "cmd-1");
        target.BindLiveObservation(lease, new FakeLiveSinkLease("run-1"), new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1"),
            new Aevatar.CQRS.Core.Abstractions.Interactions.CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                false,
                WorkflowProjectionCompletionStatus.Unknown,
                Aevatar.CQRS.Core.Abstractions.Interactions.CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task PublishDetachedCommandSignalAsync_WhenCompleted_ShouldReleaseDestroyActorsAndSkipDurableQuery()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var queryPort = new FakeCurrentStateQueryPort
        {
            Snapshot = new WorkflowActorSnapshot { CompletionStatus = WorkflowRunCompletionStatus.Completed },
        };
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            currentStateQueryPort: queryPort,
            createdActorIds: ["definition-1", "run-1"]);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());
        var receipt = new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1");

        await target.PublishDetachedCommandSignalAsync(
            new DetachedCommandCompleted<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>(
                receipt,
                WorkflowProjectionCompletionStatus.Completed),
            CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        actorPort.DestroyCalls.Should().Equal("run-1", "definition-1");
        queryPort.ActorIds.Should().BeEmpty();
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task PublishDetachedCommandSignalAsync_WhenTimeoutDurableIncomplete_ShouldReleaseWithoutDestroyingActors()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var queryPort = new FakeCurrentStateQueryPort
        {
            Snapshot = new WorkflowActorSnapshot { CompletionStatus = WorkflowRunCompletionStatus.Running },
        };
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            currentStateQueryPort: queryPort,
            createdActorIds: ["definition-1", "run-1"]);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());
        var receipt = new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1");

        await target.PublishDetachedCommandSignalAsync(
            new DetachedCommandTimeout<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>(
                receipt,
                WorkflowProjectionCompletionStatus.Unknown),
            CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        actorPort.DestroyCalls.Should().BeEmpty();
        queryPort.ActorIds.Should().Equal("run-1");
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task PublishDetachedCommandSignalAsync_WhenTimeoutDurableTerminal_ShouldReleaseAndDestroyActors()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var queryPort = new FakeCurrentStateQueryPort
        {
            Snapshot = new WorkflowActorSnapshot { CompletionStatus = WorkflowRunCompletionStatus.Failed },
        };
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            currentStateQueryPort: queryPort,
            createdActorIds: ["definition-1", "run-1"]);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());
        var receipt = new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1");

        await target.PublishDetachedCommandSignalAsync(
            new DetachedCommandTimeout<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>(
                receipt,
                WorkflowProjectionCompletionStatus.Unknown),
            CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        actorPort.DestroyCalls.Should().Equal("run-1", "definition-1");
        queryPort.ActorIds.Should().Equal("run-1");
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task PublishDetachedCommandSignalAsync_WhenUnknownDetachedSignal_ShouldUseUnknownAndDurableFallback()
    {
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var queryPort = new FakeCurrentStateQueryPort
        {
            Snapshot = new WorkflowActorSnapshot { CompletionStatus = WorkflowRunCompletionStatus.Stopped },
        };
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            currentStateQueryPort: queryPort,
            createdActorIds: ["definition-1", "run-1"]);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());
        var receipt = new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1");

        await target.PublishDetachedCommandSignalAsync(
            new UnknownDetachedSignal(receipt),
            CancellationToken.None);

        queryPort.ActorIds.Should().Equal("run-1");
        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        actorPort.DestroyCalls.Should().Equal("run-1", "definition-1");
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_WhenTerminalAndMaterializationConfirmed_ShouldReclaimCreatedActors()
    {
        // 06-20-observatory-run-state-feed (R2): a completed ad-hoc run releases the lease/sink in-request
        // (never destroys synchronously), then the gated reclaim destroys the throwaway actors only after the
        // current-state doc is confirmed materialized (watermark >= head version).
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var gate = CreateReclaimGate(headVersion: 5, materializedVersion: 5);
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            createdActorIds: ["definition-1", "run-1"],
            reclaimGate: gate);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1"),
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                true,
                WorkflowProjectionCompletionStatus.Completed,
                CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);
        await target.PendingReclaimTask;

        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        actorPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_WhenTerminalButMaterializationNotConfirmed_ShouldNotDestroyCreatedActors()
    {
        // 06-20-observatory-run-state-feed (R2b): on watermark-unreached the gate DEFERS — the throwaway
        // actors are intentionally left persisted (no silent current-state doc loss), and the run does not fail.
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var gate = CreateReclaimGate(headVersion: 9, materializedVersion: 2, maxPolls: 2);
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            createdActorIds: ["definition-1", "run-1"],
            reclaimGate: gate);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1"),
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                true,
                WorkflowProjectionCompletionStatus.Completed,
                CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);
        await target.PendingReclaimTask;

        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_WhenMaterializationScopeAbsent_ShouldNotDestroyCreatedActors()
    {
        // 06-20-observatory-run-state-feed (R2b): an absent materialization status scope (null watermark) →
        // defer, never destroy on unconfirmed materialization.
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var gate = CreateReclaimGate(headVersion: 4, materializedVersion: null, maxPolls: 2);
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            createdActorIds: ["definition-1", "run-1"],
            reclaimGate: gate);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1"),
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                true,
                WorkflowProjectionCompletionStatus.Completed,
                CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);
        await target.PendingReclaimTask;

        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAfterInteractionAsync_WhenNonTerminalWithGate_ShouldNotScheduleReclaim()
    {
        // A still-running run never schedules reclaim regardless of the gate.
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var gate = CreateReclaimGate(headVersion: 5, materializedVersion: 5);
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            createdActorIds: ["definition-1", "run-1"],
            reclaimGate: gate);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1"),
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                false,
                WorkflowProjectionCompletionStatus.Unknown,
                CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);
        await target.PendingReclaimTask;

        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveSeededTarget_WhenTerminalAndMaterializationConfirmed_ShouldReclaimCreatedActors()
    {
        // 06-20-observatory-run-state-feed (R2, codex DIFF review §10 C5): /api/chat pre-creates the ad-hoc
        // run and dispatches it via WorkflowRunTargetSeed, so the SEEDED resolver branch is the real ad-hoc
        // teardown. It MUST thread the reclaim gate into the constructed target — otherwise the throwaway
        // actors are destroyed immediately and the current-state doc is dropped before it materializes. This
        // regression proves the seeded path is gated: a confirmed watermark reclaims after release.
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var gate = CreateReclaimGate(headVersion: 5, materializedVersion: 5);
        var resolver = CreateResolver(projectionPort, actorPort, gate);
        var seededRequest = CreateSeededRequest(["definition-1", "run-1"]);

        var resolution = await resolver.ResolveAsync(seededRequest, CancellationToken.None);

        resolution.Succeeded.Should().BeTrue();
        var target = resolution.Target!;
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1"),
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                true,
                WorkflowProjectionCompletionStatus.Completed,
                CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);
        await target.PendingReclaimTask;

        actorPort.DestroyCalls.Should().Equal("run-1", "definition-1");
    }

    [Fact]
    public async Task ResolveSeededTarget_WhenTerminalButMaterializationNotConfirmed_ShouldNotDestroyCreatedActors()
    {
        // 06-20-observatory-run-state-feed (R2, codex DIFF review §10 C5+R2b): the seeded path is gated, so an
        // unconfirmed watermark DEFERS — the throwaway actors are left persisted (no silent doc loss). Before
        // the C5 fix this seeded target had no gate and destroyed immediately regardless of materialization.
        var projectionPort = new FakeProjectionPort();
        var actorPort = new FakeWorkflowRunActorPort();
        var gate = CreateReclaimGate(headVersion: 9, materializedVersion: 2, maxPolls: 2);
        var resolver = CreateResolver(projectionPort, actorPort, gate);
        var seededRequest = CreateSeededRequest(["definition-1", "run-1"]);

        var resolution = await resolver.ResolveAsync(seededRequest, CancellationToken.None);

        resolution.Succeeded.Should().BeTrue();
        var target = resolution.Target!;
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());

        await target.ReleaseAfterInteractionAsync(
            new WorkflowChatRunAcceptedReceipt("run-1", "direct", "cmd-1", "corr-1"),
            new CommandInteractionCleanupContext<WorkflowProjectionCompletionStatus>(
                true,
                WorkflowProjectionCompletionStatus.Completed,
                CommandDurableCompletionObservation<WorkflowProjectionCompletionStatus>.Incomplete),
            CancellationToken.None);
        await target.PendingReclaimTask;

        actorPort.DestroyCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldDisposeSinkAndReleaseLease_WhenOnlyOneSideBound()
    {
        var projectionPort = new FakeProjectionPort();
        var target = CreateTarget(projectionPort);
        var lease = new FakeProjectionLease("run-1", "cmd-1");
        var sink = new FakeEventSink();
        target.BindLiveObservation(lease, new FakeLiveSinkLease("run-1"), sink);
        target.BindLiveObservation(lease, new FakeLiveSinkLease("run-1"), sink);

        await target.ReleaseAsync(destroyCreatedActors: false, ct: CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1", "release:run-1");
        sink.DisposeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DetachLiveObservationAsync_ShouldDetachAndDisposeSink_WithoutReleasingLease()
    {
        var projectionPort = new FakeProjectionPort();
        var target = CreateTarget(projectionPort);
        var lease = new FakeProjectionLease("run-1", "cmd-1");
        var sink = new FakeEventSink();
        target.BindLiveObservation(lease, new FakeLiveSinkLease("run-1"), sink);

        await target.DetachLiveObservationAsync(CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:run-1");
        sink.DisposeCalls.Should().Be(1);
        target.LiveSink.Should().BeNull();
        target.ProjectionLease.Should().BeSameAs(lease);
    }

    [Fact]
    public async Task DetachLiveObservationAsync_WhenNoLiveSinkIsBound_ShouldNoopWithoutProjectionCalls()
    {
        var projectionPort = new FakeProjectionPort();
        var target = CreateTarget(projectionPort);

        await target.DetachLiveObservationAsync(CancellationToken.None);

        projectionPort.Events.Should().BeEmpty();
        target.LiveSink.Should().BeNull();
        target.ProjectionLease.Should().BeNull();
    }

    [Fact]
    public async Task DetachLiveObservationAsync_WhenLiveSinkLeaseIsNull_ShouldDetachWithExplicitNullLease()
    {
        var projectionPort = new FakeProjectionPort();
        var sink = new FakeEventSink();
        var lease = new FakeProjectionLease("run-1", "cmd-1");
        var target = CreateTarget(projectionPort);
        target.BindLiveObservation(lease, null, sink);

        await target.DetachLiveObservationAsync(CancellationToken.None);

        projectionPort.Events.Should().Equal("detach:");
        sink.DisposeCalls.Should().Be(1);
        target.LiveSink.Should().BeNull();
        target.ProjectionLease.Should().BeSameAs(lease);
    }

    [Fact]
    public async Task CleanupAfterDispatchFailureAsync_ShouldAggregateCleanupFailures()
    {
        var projectionPort = new FakeProjectionPort
        {
            DetachException = new InvalidOperationException("detach failed"),
        };
        var actorPort = new FakeWorkflowRunActorPort
        {
            DestroyException = new InvalidOperationException("destroy failed"),
        };
        var target = CreateTarget(
            projectionPort: projectionPort,
            actorPort: actorPort,
            createdActorIds: ["definition-1"]);
        target.BindLiveObservation(new FakeProjectionLease("run-1", "cmd-1"), new FakeLiveSinkLease("run-1"), new FakeEventSink());

        var act = async () => await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("detach failed");
        actorPort.DestroyCalls.Should().Equal("definition-1");
    }

    [Fact]
    public async Task RollbackCreatedActorsAsync_ShouldSkipWhitespaceAndDuplicates()
    {
        var actorPort = new FakeWorkflowRunActorPort();
        var target = CreateTarget(actorPort: actorPort, createdActorIds: [" ", "run-1", "run-1", "definition-1"]);

        await target.RollbackCreatedActorsAsync(CancellationToken.None);
        await target.RollbackCreatedActorsAsync(CancellationToken.None);

        actorPort.DestroyCalls.Should().Equal("definition-1", "run-1");
    }

    [Theory]
    [InlineData(WorkflowRunEventEnvelope.EventOneofCase.RunFinished, true, WorkflowProjectionCompletionStatus.Completed)]
    [InlineData(WorkflowRunEventEnvelope.EventOneofCase.RunError, true, WorkflowProjectionCompletionStatus.Failed)]
    [InlineData(WorkflowRunEventEnvelope.EventOneofCase.RunStopped, true, WorkflowProjectionCompletionStatus.Stopped)]
    [InlineData(WorkflowRunEventEnvelope.EventOneofCase.None, false, WorkflowProjectionCompletionStatus.Unknown)]
    public void WorkflowRunCompletionPolicy_ShouldResolveTerminalStatus(
        WorkflowRunEventEnvelope.EventOneofCase eventCase,
        bool expectedResolved,
        WorkflowProjectionCompletionStatus expectedStatus)
    {
        var policy = new WorkflowRunCompletionPolicy();
        var evt = eventCase switch
        {
            WorkflowRunEventEnvelope.EventOneofCase.RunFinished => new WorkflowRunEventEnvelope
            {
                RunFinished = new WorkflowRunFinishedEventPayload(),
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunError => new WorkflowRunEventEnvelope
            {
                RunError = new WorkflowRunErrorEventPayload(),
            },
            WorkflowRunEventEnvelope.EventOneofCase.RunStopped => new WorkflowRunEventEnvelope
            {
                RunStopped = new WorkflowRunStoppedEventPayload(),
            },
            _ => new WorkflowRunEventEnvelope(),
        };

        var resolved = policy.TryResolve(evt, out var status);

        resolved.Should().Be(expectedResolved);
        status.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task NoopWorkflowRunReportExporter_ShouldHonorCancellation()
    {
        var exporter = new NoopWorkflowRunReportExporter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await exporter.ExportAsync(new WorkflowRunReport(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NoopWorkflowRunReportExporter_ShouldCompleteWithoutSideEffects()
    {
        IWorkflowRunReportExportPort exporter = new NoopWorkflowRunReportExporter();

        await exporter.ExportAsync(new WorkflowRunReport(), CancellationToken.None);
    }

    private static WorkflowRunCommandTarget CreateTarget(
        FakeProjectionPort? projectionPort = null,
        FakeWorkflowRunActorPort? actorPort = null,
        FakeCurrentStateQueryPort? currentStateQueryPort = null,
        IReadOnlyList<string>? createdActorIds = null,
        WorkflowRunMaterializationReclaimGate? reclaimGate = null)
    {
        projectionPort ??= new FakeProjectionPort();
        actorPort ??= new FakeWorkflowRunActorPort();
        currentStateQueryPort ??= new FakeCurrentStateQueryPort();
        return new WorkflowRunCommandTarget("run-1",
            "direct",
            createdActorIds ?? [],
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(currentStateQueryPort),
            reclaimGate: reclaimGate,
            // 06-20-observatory-run-state-feed (R2): run the detached reclaim inline so the scheduled
            // reclaim completes deterministically within the test (no Task.Delay / no fire-and-forget race).
            detachedReclaimLauncher: reclaim => reclaim());
    }

    private static WorkflowRunCommandTargetResolver CreateResolver(
        FakeProjectionPort projectionPort,
        FakeWorkflowRunActorPort actorPort,
        WorkflowRunMaterializationReclaimGate? reclaimGate)
    {
        return new WorkflowRunCommandTargetResolver(
            new FakeWorkflowRunActorResolver(),
            projectionPort,
            actorPort,
            new WorkflowRunDurableCompletionResolver(new FakeCurrentStateQueryPort()),
            reclaimGate);
    }

    private static WorkflowChatRunRequest CreateSeededRequest(IReadOnlyList<string> createdActorIds) =>
        new(
            "hello",
            WorkflowChatSource.CatalogWorkflow("direct"),
            ExternalCapabilityExecutionMode.Interactive,
            TargetSeed: new WorkflowRunTargetSeed(
                "run-1",
                "direct",
                createdActorIds,
                WorkflowChatSource.CatalogWorkflow("direct")));

    private static WorkflowRunMaterializationReclaimGate CreateReclaimGate(
        long? headVersion,
        long? materializedVersion,
        int maxPolls = 3)
    {
        return new WorkflowRunMaterializationReclaimGate(
            new FakeCommittedVersionPort(headVersion),
            new FakeMaterializationWatermarkPort(materializedVersion),
            new WorkflowRunReclaimOptions
            {
                MaxWatermarkPolls = maxPolls,
                WatermarkPollInterval = TimeSpan.Zero,
            },
            delayAsync: (_, _) => Task.CompletedTask);
    }

    private sealed class FakeWorkflowRunActorResolver : IWorkflowRunActorResolver
    {
        // The seeded resolver branch never calls the actor resolver (it short-circuits on TargetSeed); this
        // fake only satisfies the constructor.
        public Task<WorkflowActorResolutionResult> ResolveOrCreateAsync(
            WorkflowChatRunRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Seeded resolution must not call the actor resolver.");
    }

    private sealed class FakeCommittedVersionPort(long? version) : IWorkflowRunCommittedVersionPort
    {
        public Task<long?> GetCommittedVersionAsync(string runActorId, CancellationToken ct = default) =>
            Task.FromResult(version);
    }

    private sealed class FakeMaterializationWatermarkPort(long? version) : IWorkflowRunMaterializationWatermarkPort
    {
        public Task<long?> GetMaterializedVersionAsync(string runActorId, CancellationToken ct = default) =>
            Task.FromResult(version);
    }

    private sealed class FakeProjectionPort
        : IWorkflowExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public Exception? DetachException { get; set; }
        public Exception? ReleaseException { get; set; }
        public List<string> Events { get; } = [];
        public Task<IAsyncDisposable?> AttachLiveSinkAsync(IWorkflowExecutionProjectionLease lease, IEventSink<WorkflowRunEventEnvelope> sink, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<EventSinkProjectionAttachment<IWorkflowExecutionProjectionLease>?> AttachExistingActorProjectionAsync(
            string rootActorId,
            string commandId,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DetachLiveSinkAsync(IAsyncDisposable? liveSinkLease, CancellationToken ct = default)
        {
            var actorId = liveSinkLease is FakeLiveSinkLease fakeLease ? fakeLease.ActorId : string.Empty;
            Events.Add($"detach:{actorId}");
            if (DetachException != null)
                throw DetachException;
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(IWorkflowExecutionProjectionLease lease, CancellationToken ct = default)
        {
            Events.Add($"release:{lease.ActorId}");
            if (ReleaseException != null)
                throw ReleaseException;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjectionLease(string actorId, string commandId) : IWorkflowExecutionProjectionLease
    {
        public string ActorId { get; } = actorId;
        public string CommandId { get; } = commandId;
    }

    private sealed class FakeEventSink : IEventSink<WorkflowRunEventEnvelope>
    {
        public int DisposeCalls { get; private set; }

        public void Push(WorkflowRunEventEnvelope evt) => throw new NotSupportedException();

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default) => throw new NotSupportedException();

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record FakeLiveSinkLease(string ActorId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWorkflowRunActorPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public Exception? DestroyException { get; set; }
        public List<string> DestroyCalls { get; } = [];
        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            DestroyCalls.Add(actorId);
            if (DestroyException != null)
                throw DestroyException;
            return Task.CompletedTask;
        }
        public Task MarkStoppedAsync(
            string actorId,
            string runId,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed record UnknownDetachedSignal(WorkflowChatRunAcceptedReceipt Receipt)
        : DetachedCommandSignal<WorkflowChatRunAcceptedReceipt, WorkflowProjectionCompletionStatus>(Receipt);

    private sealed class FakeCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
    {
        public WorkflowActorSnapshot? Snapshot { get; set; }
        public List<string> ActorIds { get; } = [];
        public bool WorkflowActorCurrentStateQueryEnabled => true;

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ActorIds.Add(actorId);
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(int take = 200, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new FakeAgent(id + "-agent");

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("fake");

        public Task<IReadOnlyList<global::System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<global::System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
