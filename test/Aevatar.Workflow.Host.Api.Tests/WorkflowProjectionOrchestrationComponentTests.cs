using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowProjectionOrchestrationComponentTests
{
    [Fact]
    public async Task ActivationService_ShouldStartProjectionAndReturnRuntimeLease()
    {
        var lifecycle = new RecordingLifecycleService();
        var ownership = new TrackingOwnershipCoordinator();
        var activationService = CreateActivationService(
            lifecycle,
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownership);

        var lease = await activationService.EnsureAsync(
            "actor-activation",
            "direct",
            "hello",
            "cmd-activation",
            CancellationToken.None);

        lease.ActorId.Should().Be("actor-activation");
        lease.CommandId.Should().Be("cmd-activation");
        lifecycle.StartedContexts.Should().ContainSingle();
        ownership.Acquired.Should().ContainSingle().Which.Should().Be(("actor-activation", "cmd-activation"));
    }

    [Fact]
    public async Task ActivationService_WhenStartFails_ShouldReleaseOwnershipAndRethrow()
    {
        var ownership = new TrackingOwnershipCoordinator();
        var activationService = CreateActivationService(
            new ThrowingLifecycleService(new InvalidOperationException("start failed")),
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownership);

        var act = async () => await activationService.EnsureAsync(
            "actor-fail",
            "direct",
            "hello",
            "cmd-fail",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("start failed");
        ownership.Acquired.Should().ContainSingle().Which.Should().Be(("actor-fail", "cmd-fail"));
        ownership.Released.Should().ContainSingle().Which.Should().Be(("actor-fail", "cmd-fail"));
    }

    [Fact]
    public async Task ActivationService_ShouldNotWriteReportArtifactDuringStartup()
    {
        var lifecycle = new RecordingLifecycleService();
        var ownership = new TrackingOwnershipCoordinator();
        var activationService = CreateActivationService(
            lifecycle,
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownership);

        var lease = await activationService.EnsureAsync(
            "actor-clean-start",
            "direct",
            "hello",
            "cmd-clean-start",
            CancellationToken.None);

        lease.ActorId.Should().Be("actor-clean-start");
        lifecycle.StartedContexts.Should().ContainSingle();
        lifecycle.StoppedContexts.Should().BeEmpty();
        ownership.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task ActivationService_ShouldWaitForProjectionReleaseListenerReadinessBeforeReturningLease()
    {
        var lifecycle = new RecordingLifecycleService();
        var ownership = new TrackingOwnershipCoordinator();
        var projectionControlHub = new BlockingProjectionControlHub();
        var activationService = CreateActivationService(
            lifecycle,
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownership,
            projectionControlHub: projectionControlHub);

        var ensureTask = activationService.EnsureAsync(
            "actor-ready",
            "direct",
            "hello",
            "cmd-ready",
            CancellationToken.None);

        await projectionControlHub.SubscribeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ensureTask.IsCompleted.Should().BeFalse();

        projectionControlHub.AllowSubscribe.TrySetResult(true);
        var lease = await ensureTask.WaitAsync(TimeSpan.FromSeconds(5));

        lease.ActorId.Should().Be("actor-ready");
        lease.CommandId.Should().Be("cmd-ready");
        await lease.StopProjectionReleaseListenerAsync();
        await lease.StopOwnershipHeartbeatAsync();
    }

    [Fact]
    public async Task ActivationAndReleaseServices_ShouldRenewOwnershipLease_AndStopHeartbeatBeforeRelease()
    {
        var lifecycle = new RecordingLifecycleService();
        var ownership = new TrackingOwnershipCoordinator();
        var ownershipOptions = new ProjectionOwnershipCoordinatorOptions
        {
            LeaseTtlMs = ProjectionOwnershipCoordinatorOptions.MinimumLeaseTtlMs,
        };
        var activationService = CreateActivationService(
            lifecycle,
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownership,
            ownershipOptions: ownershipOptions);
        var releaseService = new ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            lifecycle);

        var lease = await activationService.EnsureAsync(
            "actor-heartbeat",
            "direct",
            "hello",
            "cmd-heartbeat",
            CancellationToken.None);
        await ownership.WaitForAcquireCountAsync(2, TimeSpan.FromSeconds(3));

        await releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None);

        ownership.Acquired.Count.Should().BeGreaterThanOrEqualTo(2);
        ownership.Released.Should().ContainSingle().Which.Should().Be(("actor-heartbeat", "cmd-heartbeat"));
        ownership.Operations[^1].Should().Be(("release", "actor-heartbeat", "cmd-heartbeat"));
    }

    [Fact]
    public async Task RuntimeLease_ShouldIgnoreNonMatchingControlEvents_AndStopOnlyOnceForMatchingRelease()
    {
        var lifecycle = new RecordingLifecycleService();
        var projectionControlHub = new RecordingProjectionControlHub();
        var lease = CreateLease("actor-release", "cmd-release", lifecycle, projectionControlHub);

        await lease.WaitForProjectionReleaseListenerReadyAsync();
        await projectionControlHub.PublishAsync(
            "actor-release",
            "cmd-release",
            new WorkflowProjectionControlEvent(),
            CancellationToken.None);
        await projectionControlHub.PublishAsync(
            "actor-release",
            "cmd-release",
            new WorkflowProjectionControlEvent
            {
                ReleaseRequested = new WorkflowProjectionReleaseRequestedEvent
                {
                    ActorId = "other-actor",
                    CommandId = "cmd-release",
                },
            },
            CancellationToken.None);
        await projectionControlHub.PublishAsync(
            "actor-release",
            "cmd-release",
            new WorkflowProjectionControlEvent
            {
                ReleaseRequested = new WorkflowProjectionReleaseRequestedEvent
                {
                    ActorId = "actor-release",
                    CommandId = "cmd-release",
                },
            },
            CancellationToken.None);
        await projectionControlHub.PublishAsync(
            "actor-release",
            "cmd-release",
            new WorkflowProjectionControlEvent
            {
                ReleaseRequested = new WorkflowProjectionReleaseRequestedEvent
                {
                    ActorId = "actor-release",
                    CommandId = "cmd-release",
                },
            },
            CancellationToken.None);

        lifecycle.StoppedContexts.Should().ContainSingle();
        lifecycle.StoppedContexts.Single().RootActorId.Should().Be("actor-release");
        await lease.StopProjectionReleaseListenerAsync();
    }

    [Fact]
    public async Task RuntimeLease_WhenLifecycleStopFails_ShouldAllowRetryOnNextReleaseRequest()
    {
        var lifecycle = new RetryableStopLifecycleService();
        var projectionControlHub = new RecordingProjectionControlHub();
        var lease = CreateLease("actor-retry", "cmd-retry", lifecycle, projectionControlHub);

        await lease.WaitForProjectionReleaseListenerReadyAsync();
        await projectionControlHub.PublishAsync(
            "actor-retry",
            "cmd-retry",
            new WorkflowProjectionControlEvent
            {
                ReleaseRequested = new WorkflowProjectionReleaseRequestedEvent
                {
                    ActorId = "actor-retry",
                    CommandId = "cmd-retry",
                },
            },
            CancellationToken.None);
        await projectionControlHub.PublishAsync(
            "actor-retry",
            "cmd-retry",
            new WorkflowProjectionControlEvent
            {
                ReleaseRequested = new WorkflowProjectionReleaseRequestedEvent
                {
                    ActorId = "actor-retry",
                    CommandId = "cmd-retry",
                },
            },
            CancellationToken.None);

        lifecycle.StopAttempts.Should().Be(2);
        lifecycle.StoppedContexts.Should().ContainSingle();
        await lease.StopProjectionReleaseListenerAsync();
    }

    [Fact]
    public async Task ReleaseService_ShouldReleaseOwnership_WhenMarkStoppedThrows()
    {
        var lifecycle = new RecordingLifecycleService();
        var ownership = new TrackingOwnershipCoordinator();
        var releaseService = new ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            lifecycle);
        var lease = new WorkflowExecutionRuntimeLease(
            new WorkflowExecutionProjectionContext
            {
                ProjectionId = "projection-release-fail",
                RootActorId = "actor-release-fail",
                CommandId = "cmd-release-fail",
                WorkflowName = "direct",
                StartedAt = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero),
                Input = "hello",
            },
            ownershipCoordinator: ownership);

        var act = async () => await releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None);

        await act.Should().NotThrowAsync();
        lifecycle.StoppedContexts.Should().ContainSingle();
        ownership.Released.Should().ContainSingle()
            .Which.Should().Be(("actor-release-fail", "cmd-release-fail"));
    }

    [Fact]
    public async Task QueryReader_ShouldMapSnapshotsAndSortTimeline()
    {
        var timelineStore = CreateTimelineStore();
        await timelineStore.UpsertAsync(new WorkflowRunTimelineDocument
        {
            Id = "actor-3",
            RootActorId = "actor-3",
            CommandId = "cmd-3",
            UpdatedAt = new DateTimeOffset(2026, 2, 21, 10, 5, 0, TimeSpan.Zero),
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 2, 21, 10, 0, 1, TimeSpan.Zero),
                    Stage = "step-1",
                },
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 2, 21, 10, 0, 3, TimeSpan.Zero),
                    Stage = "step-3",
                },
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = new DateTimeOffset(2026, 2, 21, 10, 0, 2, TimeSpan.Zero),
                    Stage = "step-2",
                },
            ],
        });
        var currentStateStore = CreateCurrentStateStore();
        await currentStateStore.UpsertAsync(new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-3",
            RootActorId = "actor-3",
            CommandId = "cmd-3",
            WorkflowName = "demo",
            Status = "completed",
            FinalOutput = "done",
            Success = true,
            StateVersion = 3,
            LastEventId = "evt-3",
            UpdatedAt = new DateTimeOffset(2026, 2, 21, 10, 5, 0, TimeSpan.Zero),
        });
        var reader = new WorkflowProjectionQueryReader(
            currentStateStore,
            timelineStore,
            new WorkflowExecutionReadModelMapper(),
            new InMemoryProjectionGraphStore());

        var snapshot = await reader.GetActorSnapshotAsync("actor-3");
        var projectionState = await reader.GetActorProjectionStateAsync("actor-3");
        var timeline = await reader.ListActorTimelineAsync("actor-3", take: 2);

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("actor-3");
        projectionState.Should().NotBeNull();
        projectionState!.LastCommandId.Should().Be("cmd-3");
        snapshot.LastOutput.Should().Be("done");
        snapshot.TotalSteps.Should().Be(0);
        timeline.Should().HaveCount(2);
        timeline[0].Stage.Should().Be("step-3");
        timeline[1].Stage.Should().Be("step-2");
    }

    [Fact]
    public async Task QueryReader_GraphQueries_ShouldRespectDirectionFiltersAndBounds()
    {
        var timelineStore = CreateTimelineStore();
        var currentStateStore = CreateCurrentStateStore();
        await currentStateStore.UpsertAsync(new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-graph",
            RootActorId = "actor-graph",
            CommandId = "cmd-graph",
            WorkflowName = "direct",
            Status = "running",
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var graphStore = new InMemoryProjectionGraphStore();
        var now = DateTimeOffset.UtcNow;
        await graphStore.UpsertNodeAsync(new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = "actor-graph",
            NodeType = WorkflowExecutionGraphConstants.ActorNodeType,
            UpdatedAt = now,
        });
        await graphStore.UpsertNodeAsync(new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = "actor-child",
            NodeType = WorkflowExecutionGraphConstants.ActorNodeType,
            UpdatedAt = now,
        });
        await graphStore.UpsertNodeAsync(new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = "actor-parent",
            NodeType = WorkflowExecutionGraphConstants.ActorNodeType,
            UpdatedAt = now,
        });

        await graphStore.UpsertEdgeAsync(new ProjectionGraphEdge
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            EdgeId = "edge-out-own",
            FromNodeId = "actor-graph",
            ToNodeId = "actor-child",
            EdgeType = WorkflowExecutionGraphConstants.EdgeTypeOwns,
            UpdatedAt = now.AddMinutes(3),
        });
        await graphStore.UpsertEdgeAsync(new ProjectionGraphEdge
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            EdgeId = "edge-in-child",
            FromNodeId = "actor-parent",
            ToNodeId = "actor-graph",
            EdgeType = WorkflowExecutionGraphConstants.EdgeTypeChildOf,
            UpdatedAt = now.AddMinutes(2),
        });
        await graphStore.UpsertEdgeAsync(new ProjectionGraphEdge
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            EdgeId = "edge-out-child",
            FromNodeId = "actor-graph",
            ToNodeId = "actor-parent",
            EdgeType = WorkflowExecutionGraphConstants.EdgeTypeChildOf,
            UpdatedAt = now.AddMinutes(1),
        });

        var reader = new WorkflowProjectionQueryReader(
            currentStateStore,
            timelineStore,
            new WorkflowExecutionReadModelMapper(),
            graphStore);

        var outboundOwn = await reader.GetActorGraphEdgesAsync(
            " actor-graph ",
            take: 0,
            options: new WorkflowActorGraphQueryOptions
            {
                Direction = WorkflowActorGraphDirection.Outbound,
                EdgeTypes = [" OWNS ", "OWNS", ""],
            });
        outboundOwn.Should().ContainSingle();
        outboundOwn[0].EdgeId.Should().Be("edge-out-own");

        var inbound = await reader.GetActorGraphEdgesAsync(
            "actor-graph",
            take: 50,
            options: new WorkflowActorGraphQueryOptions
            {
                Direction = WorkflowActorGraphDirection.Inbound,
            });
        inbound.Should().ContainSingle();
        inbound[0].EdgeId.Should().Be("edge-in-child");

        var both = await reader.GetActorGraphEdgesAsync("actor-graph", take: 50);
        both.Should().HaveCount(3);

        var emptyActor = await reader.GetActorGraphEdgesAsync("   ", take: 50);
        emptyActor.Should().BeEmpty();

        var subgraph = await reader.GetActorGraphSubgraphAsync(
            "actor-graph",
            depth: 0,
            take: 0,
            options: new WorkflowActorGraphQueryOptions
            {
                Direction = WorkflowActorGraphDirection.Both,
                EdgeTypes = [WorkflowExecutionGraphConstants.EdgeTypeChildOf],
            });
        subgraph.RootNodeId.Should().Be("actor-graph");
        subgraph.Edges.Should().HaveCount(1);
        subgraph.Edges.Should().OnlyContain(x => x.EdgeType == WorkflowExecutionGraphConstants.EdgeTypeChildOf);
        subgraph.Nodes.Should().Contain(x => x.NodeId == "actor-graph");
    }

    [Fact]
    public async Task QueryReader_GetActorGraphEnrichedSnapshotAsync_ShouldReturnNullForMissingSnapshot_AndComposeWhenExists()
    {
        var timelineStore = CreateTimelineStore();
        var currentStateStore = CreateCurrentStateStore();
        var graphStore = new InMemoryProjectionGraphStore();
        var reader = new WorkflowProjectionQueryReader(
            currentStateStore,
            timelineStore,
            new WorkflowExecutionReadModelMapper(),
            graphStore);

        var missing = await reader.GetActorGraphEnrichedSnapshotAsync("missing");
        missing.Should().BeNull();

        var now = DateTimeOffset.UtcNow;
        await currentStateStore.UpsertAsync(new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-enriched",
            RootActorId = "actor-enriched",
            CommandId = "cmd-enriched",
            WorkflowName = "wf-enriched",
            Status = "running",
            UpdatedAt = now,
        });
        await graphStore.UpsertNodeAsync(new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = "actor-enriched",
            NodeType = WorkflowExecutionGraphConstants.ActorNodeType,
            UpdatedAt = now,
        });

        var enriched = await reader.GetActorGraphEnrichedSnapshotAsync("actor-enriched");
        enriched.Should().NotBeNull();
        enriched!.Snapshot.ActorId.Should().Be("actor-enriched");
        enriched.Subgraph.RootNodeId.Should().Be("actor-enriched");
        enriched.Subgraph.Nodes.Should().ContainSingle(x => x.NodeId == "actor-enriched");
    }

    [Fact]
    public async Task QueryReader_ListSnapshotsAndTimeline_ShouldClampTakeAndHandleMissingActor()
    {
        var now = DateTimeOffset.UtcNow;
        var timelineStore = CreateTimelineStore();
        var currentStateStore = CreateCurrentStateStore();
        await timelineStore.UpsertAsync(new WorkflowRunTimelineDocument
        {
            Id = "actor-a",
            RootActorId = "actor-a",
            CommandId = "cmd-a",
            UpdatedAt = now.AddMinutes(-1),
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = now.AddMinutes(-1),
                    Stage = "s-1",
                    Data = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" },
                },
            ],
        });
        await currentStateStore.UpsertAsync(new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-a",
            RootActorId = "actor-a",
            CommandId = "cmd-a",
            WorkflowName = "wf",
            Status = "running",
            UpdatedAt = now.AddMinutes(-1),
        });
        await currentStateStore.UpsertAsync(new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-b",
            RootActorId = "actor-b",
            CommandId = "cmd-b",
            WorkflowName = "wf",
            Status = "running",
            UpdatedAt = now,
        });

        var reader = new WorkflowProjectionQueryReader(
            currentStateStore,
            timelineStore,
            new WorkflowExecutionReadModelMapper(),
            new InMemoryProjectionGraphStore());

        var minTake = await reader.ListActorSnapshotsAsync(take: 0);
        minTake.Should().HaveCount(1);

        var maxTake = await reader.ListActorSnapshotsAsync(take: 9999);
        maxTake.Should().HaveCount(2);

        var missingTimeline = await reader.ListActorTimelineAsync("missing", take: 10);
        missingTimeline.Should().BeEmpty();

        var timeline = await reader.ListActorTimelineAsync("actor-a", take: 10);
        timeline.Should().ContainSingle();
        timeline[0].Data.Should().ContainKey("k").WhoseValue.Should().Be("v");
    }

    [Fact]
    public void ReadModelMapper_ShouldMapSnapshotTimelineAndGraphModels_WithCopiedDictionaries()
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var now = DateTimeOffset.UtcNow;
        var currentState = new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-map",
            RootActorId = "actor-map",
            WorkflowName = "wf-map",
            CommandId = "cmd-map",
            Status = "completed",
            StateVersion = 3,
            LastEventId = "evt-map",
            UpdatedAt = now,
            Success = true,
            FinalOutput = "ok",
            FinalError = "",
        };
        var snapshot = mapper.ToActorSnapshot(currentState);
        var projectionState = mapper.ToActorProjectionState(currentState);
        snapshot.ActorId.Should().Be("actor-map");
        snapshot.WorkflowName.Should().Be("wf-map");
        snapshot.LastSuccess.Should().BeTrue();
        snapshot.LastOutput.Should().Be("ok");
        snapshot.TotalSteps.Should().Be(0);
        snapshot.RoleReplyCount.Should().Be(0);
        projectionState.ActorId.Should().Be("actor-map");
        projectionState.LastCommandId.Should().Be("cmd-map");
        projectionState.LastEventId.Should().Be("evt-map");

        var timelineSource = new WorkflowExecutionTimelineEvent
        {
            Timestamp = now,
            Stage = "stage-map",
            Message = "msg-map",
            AgentId = "agent-map",
            StepId = "step-map",
            StepType = "type-map",
            EventType = "evt-map",
            Data = new Dictionary<string, string>(StringComparer.Ordinal) { ["x"] = "1" },
        };
        var timeline = mapper.ToActorTimelineItem(timelineSource);
        timeline.Stage.Should().Be("stage-map");
        timeline.Data.Should().ContainKey("x").WhoseValue.Should().Be("1");
        timeline.Data.Should().NotBeSameAs(timelineSource.Data);

        var graphNodeSource = new ProjectionGraphNode
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            NodeId = "n-1",
            NodeType = "Actor",
            UpdatedAt = now,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal) { ["p"] = "v" },
        };
        var graphEdgeSource = new ProjectionGraphEdge
        {
            Scope = WorkflowExecutionGraphConstants.Scope,
            EdgeId = "e-1",
            FromNodeId = "n-1",
            ToNodeId = "n-2",
            EdgeType = WorkflowExecutionGraphConstants.EdgeTypeOwns,
            UpdatedAt = now,
            Properties = new Dictionary<string, string>(StringComparer.Ordinal) { ["q"] = "w" },
        };

        var graphNode = mapper.ToActorGraphNode(graphNodeSource);
        graphNode.NodeId.Should().Be("n-1");
        graphNode.Properties.Should().ContainKey("p").WhoseValue.Should().Be("v");
        graphNode.Properties.Should().NotBeSameAs(graphNodeSource.Properties);

        var graphEdge = mapper.ToActorGraphEdge(graphEdgeSource);
        graphEdge.EdgeId.Should().Be("e-1");
        graphEdge.Properties.Should().ContainKey("q").WhoseValue.Should().Be("w");
        graphEdge.Properties.Should().NotBeSameAs(graphEdgeSource.Properties);

        var subgraph = mapper.ToActorGraphSubgraph("root-map", new ProjectionGraphSubgraph
        {
            Nodes = [graphNodeSource],
            Edges = [graphEdgeSource],
        });
        subgraph.RootNodeId.Should().Be("root-map");
        subgraph.Nodes.Should().ContainSingle(x => x.NodeId == "n-1");
        subgraph.Edges.Should().ContainSingle(x => x.EdgeId == "e-1");
    }

    [Fact]
    public void ProjectionScopeEnums_ShouldRetainLegacyOrdinals()
    {
        ((int)WorkflowExecutionProjectionScope.ActorShared).Should().Be(0);
        ((int)WorkflowExecutionProjectionScope.RunIsolated).Should().Be(1);
        ((int)WorkflowRunProjectionScope.ActorShared).Should().Be(0);
        ((int)WorkflowRunProjectionScope.RunIsolated).Should().Be(1);
    }

    [Fact]
    public async Task SinkSubscriptionManager_ShouldReplaceSameSinkSubscription()
    {
        var hub = new RecordingRunEventHub();
        var manager = new EventSinkProjectionSessionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>(hub);
        var lease = CreateLease("actor-4", "cmd-4");
        var sink = new NoopRunEventSink();

        await manager.AttachOrReplaceAsync(lease, sink, _ => ValueTask.CompletedTask);
        var first = hub.Subscriptions.Should().ContainSingle().Subject;
        lease.GetLiveSinkSubscriptionCount().Should().Be(1);

        await manager.AttachOrReplaceAsync(lease, sink, _ => ValueTask.CompletedTask);
        hub.Subscriptions.Should().HaveCount(2);
        first.Disposed.Should().BeTrue();
        lease.GetLiveSinkSubscriptionCount().Should().Be(1);

        var second = hub.Subscriptions[1];
        await manager.DetachAsync(lease, sink);
        second.Disposed.Should().BeTrue();
        lease.GetLiveSinkSubscriptionCount().Should().Be(0);
    }

    [Fact]
    public async Task SinkFailurePolicy_ShouldHandleBackpressureAndCompletedAndUnknownErrors()
    {
        var sinkManager = new RecordingSinkSubscriptionManager();
        var runEventHub = new RecordingRunEventHub();
        var policy = new WorkflowProjectionSinkFailurePolicy(
            sinkManager,
            runEventHub,
            new FixedClock(new DateTimeOffset(2026, 2, 21, 9, 0, 0, TimeSpan.Zero)));
        var lease = CreateLease("actor-5", "cmd-5");
        var sink = new NoopRunEventSink();
        var sourceEvent = BuildRunStartedEvent("thread-1");

        var handledBackpressure = await policy.TryHandleAsync(
            lease,
            sink,
            sourceEvent,
            new EventSinkBackpressureException());

        handledBackpressure.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(1);
        runEventHub.PublishedEvents.Should().ContainSingle();
        runEventHub.PublishedEvents[0].evt.EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.Custom);
        runEventHub.PublishedEvents[0].evt.Custom.Name.Should().Be(WorkflowProjectionSinkFailurePolicy.ProjectionSinkFailureEventName);
        runEventHub.PublishedEvents[0].evt.Custom.Payload.Unpack<WorkflowProjectionSinkFailureCustomPayload>().Code
            .Should().Be(WorkflowProjectionSinkFailurePolicy.SinkBackpressureErrorCode);

        runEventHub.PublishedEvents.Clear();
        var handledCompleted = await policy.TryHandleAsync(
            lease,
            sink,
            sourceEvent,
            new EventSinkCompletedException());

        handledCompleted.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(2);
        runEventHub.PublishedEvents.Should().ContainSingle();
        runEventHub.PublishedEvents[0].evt.Custom.Payload.Unpack<WorkflowProjectionSinkFailureCustomPayload>().Code
            .Should().Be(WorkflowProjectionSinkFailurePolicy.SinkWriteErrorCode);

        var handledUnknown = await policy.TryHandleAsync(
            lease,
            sink,
            sourceEvent,
            new ApplicationException("unknown"));

        handledUnknown.Should().BeFalse();
        sinkManager.DetachCalls.Should().Be(2);
    }

    [Fact]
    public async Task ReleaseService_WhenNoLiveSink_ShouldStopMarkAndRelease()
    {
        var lifecycle = new RecordingLifecycleService();
        var ownership = new TrackingOwnershipCoordinator();
        var releaseService = new ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            lifecycle);
        var lease = CreateLease("actor-release", "cmd-release", ownershipCoordinator: ownership);

        await releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None);

        lifecycle.StoppedContexts.Should().ContainSingle();
        ownership.Released.Should().ContainSingle().Which.Should().Be(("actor-release", "cmd-release"));
    }

    [Fact]
    public async Task ReleaseService_WhenLiveSinkExists_ShouldSkipStopAndRelease()
    {
        var lifecycle = new RecordingLifecycleService();
        var ownership = new TrackingOwnershipCoordinator();
        var releaseService = new ContextProjectionReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>(
            lifecycle);
        var lease = CreateLease("actor-busy", "cmd-busy", ownershipCoordinator: ownership);
        lease.AttachOrReplaceLiveSinkSubscription(
            new NoopRunEventSink(),
            new TrackingSubscription());

        await releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None);

        lifecycle.StoppedContexts.Should().BeEmpty();
        ownership.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task LiveSinkForwarder_WhenSinkThrowsAndPolicyHandles_ShouldNotThrow()
    {
        var policy = new RecordingSinkFailurePolicy
        {
            NextHandledResult = true,
        };
        var forwarder = new EventSinkProjectionLiveForwarder<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>(policy);
        var sink = new ThrowingRunEventSink(new InvalidOperationException("sink failed"));
        var sourceEvent = BuildRunStartedEvent("thread-1");

        var act = async () => await forwarder.ForwardAsync(
            CreateLease("actor-forward", "cmd-forward"),
            sink,
            sourceEvent,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        policy.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task LiveSinkForwarder_WhenPolicyDoesNotHandle_ShouldRethrowOriginalError()
    {
        var policy = new RecordingSinkFailurePolicy
        {
            NextHandledResult = false,
        };
        var forwarder = new EventSinkProjectionLiveForwarder<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>(policy);
        var sink = new ThrowingRunEventSink(new InvalidOperationException("sink failed"));
        var sourceEvent = BuildRunStartedEvent("thread-1");

        var act = async () => await forwarder.ForwardAsync(
            CreateLease("actor-forward-2", "cmd-forward-2"),
            sink,
            sourceEvent,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("sink failed");
        policy.Calls.Should().ContainSingle();
    }

    private static InMemoryProjectionDocumentStore<WorkflowExecutionReport, string> CreateStore() => new(
        keySelector: report => report.RootActorId,
        keyFormatter: key => key,
        defaultSortSelector: report => report.StartedAt);

    private static InMemoryProjectionDocumentStore<WorkflowRunTimelineDocument, string> CreateTimelineStore() => new(
        keySelector: document => document.RootActorId,
        keyFormatter: key => key,
        defaultSortSelector: document => document.UpdatedAt);

    private static InMemoryProjectionDocumentStore<WorkflowExecutionCurrentStateDocument, string> CreateCurrentStateStore() => new(
        keySelector: document => document.RootActorId,
        keyFormatter: key => key,
        defaultSortSelector: document => document.UpdatedAt);

    private static ContextProjectionActivationService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> CreateActivationService(
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> lifecycle,
        IProjectionClock clock,
        IWorkflowExecutionProjectionContextFactory contextFactory,
        IProjectionOwnershipCoordinator ownershipCoordinator,
        ProjectionOwnershipCoordinatorOptions? ownershipOptions = null,
        IProjectionSessionEventHub<WorkflowProjectionControlEvent>? projectionControlHub = null) =>
        new(
            lifecycle,
            (rootEntityId, workflowName, input, commandId, _) => contextFactory.Create(
                rootEntityId,
                commandId,
                rootEntityId,
                workflowName,
                input,
                clock.UtcNow),
            context => new WorkflowExecutionRuntimeLease(
                context,
                ownershipCoordinator,
                ownershipOptions,
                lifecycle,
                projectionControlHub),
            acquireBeforeStart: (rootEntityId, _, _, commandId, ct) =>
                ownershipCoordinator.AcquireAsync(rootEntityId, commandId, ct),
            onRuntimeLeaseCreated: async (_, _, _, runtimeLease, ct) =>
            {
                try
                {
                    await runtimeLease.WaitForProjectionReleaseListenerReadyAsync(ct);
                }
                catch
                {
                    await runtimeLease.StopProjectionReleaseListenerAsync();
                    await runtimeLease.StopOwnershipHeartbeatAsync();
                    throw;
                }
            },
            cleanupOnStartFailure: async (rootEntityId, commandId) =>
            {
                try
                {
                    await ownershipCoordinator.ReleaseAsync(rootEntityId, commandId, CancellationToken.None);
                }
                catch
                {
                }
            });

    private static WorkflowExecutionRuntimeLease CreateLease(
        string actorId,
        string commandId,
        IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>? lifecycle = null,
        IProjectionSessionEventHub<WorkflowProjectionControlEvent>? projectionControlHub = null,
        IProjectionOwnershipCoordinator? ownershipCoordinator = null) => new(
        new WorkflowExecutionProjectionContext
        {
            ProjectionId = actorId,
            RootActorId = actorId,
            CommandId = commandId,
            WorkflowName = "direct",
            Input = "hello",
            StartedAt = DateTimeOffset.UtcNow,
        },
        ownershipCoordinator: ownershipCoordinator,
        lifecycle: lifecycle,
        projectionControlHub: projectionControlHub);

    private static WorkflowRunEventEnvelope BuildRunStartedEvent(string threadId) =>
        new()
        {
            RunStarted = new WorkflowRunStartedEventPayload
            {
                ThreadId = threadId,
            },
        };

    private sealed class TrackingOwnershipCoordinator : IProjectionOwnershipCoordinator
    {
        private readonly object _gate = new();
        private readonly List<(string ScopeId, string SessionId)> _acquired = [];
        private readonly List<(string ScopeId, string SessionId)> _released = [];
        private readonly List<(string Kind, string ScopeId, string SessionId)> _operations = [];
        private readonly List<(int Count, TaskCompletionSource<bool> Signal)> _acquireCountWaiters = [];

        public IReadOnlyList<(string ScopeId, string SessionId)> Acquired
        {
            get
            {
                lock (_gate)
                    return _acquired.ToArray();
            }
        }

        public IReadOnlyList<(string ScopeId, string SessionId)> Released
        {
            get
            {
                lock (_gate)
                    return _released.ToArray();
            }
        }

        public IReadOnlyList<(string Kind, string ScopeId, string SessionId)> Operations
        {
            get
            {
                lock (_gate)
                    return _operations.ToArray();
            }
        }

        public Task AcquireAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _acquired.Add((scopeId, sessionId));
                _operations.Add(("acquire", scopeId, sessionId));
                for (var i = _acquireCountWaiters.Count - 1; i >= 0; i--)
                {
                    var waiter = _acquireCountWaiters[i];
                    if (_acquired.Count < waiter.Count)
                        continue;

                    _acquireCountWaiters.RemoveAt(i);
                    waiter.Signal.TrySetResult(true);
                }
            }
            return Task.CompletedTask;
        }

        public Task<bool> HasActiveLeaseAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
                return Task.FromResult(_acquired.Contains((scopeId, sessionId)) && !_released.Contains((scopeId, sessionId)));
        }

        public Task ReleaseAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _released.Add((scopeId, sessionId));
                _operations.Add(("release", scopeId, sessionId));
            }
            return Task.CompletedTask;
        }

        public Task WaitForAcquireCountAsync(int expectedCount, TimeSpan timeout)
        {
            Task waitTask;
            lock (_gate)
            {
                if (_acquired.Count >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _acquireCountWaiters.Add((expectedCount, waiter));
                waitTask = waiter.Task;
            }

            return waitTask.WaitAsync(timeout);
        }
    }

    private sealed class FixedClock : IProjectionClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingSinkSubscriptionManager
        : IEventSinkProjectionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>
    {
        public int DetachCalls { get; private set; }

        public Task AttachOrReplaceAsync(
            WorkflowExecutionRuntimeLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            Func<WorkflowRunEventEnvelope, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DetachAsync(
            WorkflowExecutionRuntimeLease lease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            DetachCalls++;
            return Task.CompletedTask;
        }

    }
    private sealed class RecordingLifecycleService
        : IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        public List<WorkflowExecutionProjectionContext> StartedContexts { get; } = [];
        public List<WorkflowExecutionProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StartedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task ProjectAsync(
            WorkflowExecutionProjectionContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            WorkflowExecutionProjectionContext context,
            IReadOnlyList<WorkflowExecutionTopologyEdge> completion,
            CancellationToken ct = default)
        {
            _ = context;
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RetryableStopLifecycleService
        : IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        public int StopAttempts { get; private set; }
        public List<WorkflowExecutionProjectionContext> StoppedContexts { get; } = [];

        public Task StartAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ProjectAsync(
            WorkflowExecutionProjectionContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StopAttempts++;
            if (StopAttempts == 1)
                throw new InvalidOperationException("stop failed");

            StoppedContexts.Add(context);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            WorkflowExecutionProjectionContext context,
            IReadOnlyList<WorkflowExecutionTopologyEdge> completion,
            CancellationToken ct = default)
        {
            _ = context;
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLifecycleService
        : IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
    {
        private readonly Exception _startException;

        public ThrowingLifecycleService(Exception startException)
        {
            _startException = startException;
        }

        public Task StartAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.FromException(_startException);
        }

        public Task ProjectAsync(
            WorkflowExecutionProjectionContext context,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = context;
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default)
        {
            _ = context;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CompleteAsync(
            WorkflowExecutionProjectionContext context,
            IReadOnlyList<WorkflowExecutionTopologyEdge> completion,
            CancellationToken ct = default)
        {
            _ = context;
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSinkFailurePolicy
        : IEventSinkProjectionFailurePolicy<WorkflowExecutionRuntimeLease, WorkflowRunEventEnvelope>
    {
        public bool NextHandledResult { get; set; }
        public List<(WorkflowExecutionRuntimeLease Lease, IEventSink<WorkflowRunEventEnvelope> Sink, WorkflowRunEventEnvelope Event, Exception Exception)> Calls { get; } = [];

        public ValueTask<bool> TryHandleAsync(
            WorkflowExecutionRuntimeLease runtimeLease,
            IEventSink<WorkflowRunEventEnvelope> sink,
            WorkflowRunEventEnvelope sourceEvent,
            Exception exception,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((runtimeLease, sink, sourceEvent, exception));
            return ValueTask.FromResult(NextHandledResult);
        }
    }

    private sealed class ThrowingRunEventSink : IEventSink<WorkflowRunEventEnvelope>
    {
        private readonly Exception _exception;

        public ThrowingRunEventSink(Exception exception)
        {
            _exception = exception;
        }

        public void Push(WorkflowRunEventEnvelope evt)
        {
            _ = evt;
            throw _exception;
        }

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            throw _exception;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRunEventHub : IProjectionSessionEventHub<WorkflowRunEventEnvelope>
    {
        public List<(string scopeId, string sessionId, WorkflowRunEventEnvelope evt)> PublishedEvents { get; } = [];
        public List<TrackingSubscription> Subscriptions { get; } = [];

        public Task PublishAsync(
            string scopeId,
            string sessionId,
            WorkflowRunEventEnvelope evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            PublishedEvents.Add((scopeId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowRunEventEnvelope, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            var subscription = new TrackingSubscription();
            Subscriptions.Add(subscription);
            return Task.FromResult<IAsyncDisposable>(subscription);
        }
    }

    private sealed class BlockingProjectionControlHub : IProjectionSessionEventHub<WorkflowProjectionControlEvent>
    {
        public TaskCompletionSource<bool> SubscribeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowSubscribe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task PublishAsync(
            string scopeId,
            string sessionId,
            WorkflowProjectionControlEvent evt,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public async Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowProjectionControlEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            SubscribeEntered.TrySetResult(true);
            await AllowSubscribe.Task.WaitAsync(ct);
            return new TrackingSubscription();
        }
    }

    private sealed class RecordingProjectionControlHub : IProjectionSessionEventHub<WorkflowProjectionControlEvent>
    {
        private readonly Dictionary<(string ScopeId, string SessionId), List<Func<WorkflowProjectionControlEvent, ValueTask>>> _handlers = new();

        public Task PublishAsync(
            string scopeId,
            string sessionId,
            WorkflowProjectionControlEvent evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_handlers.TryGetValue((scopeId, sessionId), out var handlers))
                return Task.CompletedTask;

            return PublishCoreAsync(handlers.ToArray(), evt, ct);
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<WorkflowProjectionControlEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var key = (scopeId, sessionId);
            if (!_handlers.TryGetValue(key, out var handlers))
            {
                handlers = [];
                _handlers[key] = handlers;
            }

            handlers.Add(handler);
            return Task.FromResult<IAsyncDisposable>(new ProjectionControlSubscription(_handlers, key, handler));
        }

        private static async Task PublishCoreAsync(
            IReadOnlyList<Func<WorkflowProjectionControlEvent, ValueTask>> handlers,
            WorkflowProjectionControlEvent evt,
            CancellationToken ct)
        {
            foreach (var handler in handlers)
            {
                ct.ThrowIfCancellationRequested();
                await handler(evt);
            }
        }
    }

    private sealed class ProjectionControlSubscription(
        Dictionary<(string ScopeId, string SessionId), List<Func<WorkflowProjectionControlEvent, ValueTask>>> handlers,
        (string ScopeId, string SessionId) key,
        Func<WorkflowProjectionControlEvent, ValueTask> handler)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (handlers.TryGetValue(key, out var registered))
            {
                registered.Remove(handler);
                if (registered.Count == 0)
                    handlers.Remove(key);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingSubscription : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopRunEventSink : IEventSink<WorkflowRunEventEnvelope>
    {
        public void Push(WorkflowRunEventEnvelope evt)
        {
            _ = evt;
        }

        public ValueTask PushAsync(WorkflowRunEventEnvelope evt, CancellationToken ct = default)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }

        public async IAsyncEnumerable<WorkflowRunEventEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
