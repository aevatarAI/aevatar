using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
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
        var readModelUpdater = new RecordingReadModelUpdater();
        var ownership = new TrackingOwnershipCoordinator();
        var activationService = new WorkflowProjectionActivationService(
            lifecycle,
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownership,
            readModelUpdater);

        var lease = await activationService.EnsureAsync(
            "actor-activation",
            "direct",
            "hello",
            "cmd-activation",
            CancellationToken.None);

        lease.ActorId.Should().Be("actor-activation");
        lease.CommandId.Should().Be("cmd-activation");
        lifecycle.StartedContexts.Should().ContainSingle();
        readModelUpdater.Refreshed.Should().ContainSingle();
        ownership.Acquired.Should().ContainSingle().Which.Should().Be(("actor-activation", "cmd-activation"));
    }

    [Fact]
    public async Task ActivationService_WhenStartFails_ShouldReleaseOwnershipAndRethrow()
    {
        var ownership = new TrackingOwnershipCoordinator();
        var activationService = new WorkflowProjectionActivationService(
            new ThrowingLifecycleService(new InvalidOperationException("start failed")),
            new FixedClock(new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero)),
            new DefaultWorkflowExecutionProjectionContextFactory(),
            ownership,
            new RecordingReadModelUpdater());

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
    public async Task ReadModelUpdater_ShouldRefreshMetadataAndMarkStopped()
    {
        var startedAt = new DateTimeOffset(2026, 2, 21, 12, 0, 0, TimeSpan.Zero);
        var stoppedAt = startedAt.AddMinutes(3);
        var store = CreateStore();
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RootActorId = "actor-2",
            CommandId = "cmd-old",
            WorkflowName = "old-workflow",
            Input = "old-input",
            StartedAt = startedAt.AddMinutes(-5),
            EndedAt = startedAt.AddMinutes(-6),
            CompletionStatus = WorkflowExecutionCompletionStatus.Running,
        });
        var relationStore = new InMemoryProjectionGraphStore();
        var dispatcher = new ProjectionStoreDispatcher<WorkflowExecutionReport, string>(
            new IProjectionStoreBinding<WorkflowExecutionReport, string>[]
            {
                new ProjectionDocumentStoreBinding<WorkflowExecutionReport, string>(store),
                new ProjectionGraphStoreBinding<WorkflowExecutionReport, string>(relationStore),
            });
        var updater = new WorkflowProjectionReadModelUpdater(
            dispatcher,
            new FixedClock(stoppedAt));
        var context = new WorkflowExecutionProjectionContext
        {
            ProjectionId = "projection-1",
            RootActorId = "actor-2",
            CommandId = "cmd-new",
            WorkflowName = "new-workflow",
            Input = "new-input",
            StartedAt = startedAt,
        };

        await updater.RefreshMetadataAsync("actor-2", context);
        await store.MutateAsync("actor-2", report =>
        {
            report.CompletionStatus = WorkflowExecutionCompletionStatus.Running;
            report.EndedAt = report.StartedAt.AddSeconds(-1);
        });
        await updater.MarkStoppedAsync("actor-2");

        var report = await store.GetAsync("actor-2");
        report.Should().NotBeNull();
        report!.CommandId.Should().Be("cmd-new");
        report.WorkflowName.Should().Be("new-workflow");
        report.Input.Should().Be("new-input");
        report.StartedAt.Should().Be(startedAt);
        report.CompletionStatus.Should().Be(WorkflowExecutionCompletionStatus.Stopped);
        report.EndedAt.Should().Be(stoppedAt);
        report.DurationMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task QueryReader_ShouldMapSnapshotsAndSortTimeline()
    {
        var store = CreateStore();
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RootActorId = "actor-3",
            CommandId = "cmd-3",
            WorkflowName = "direct",
            StartedAt = new DateTimeOffset(2026, 2, 21, 10, 0, 0, TimeSpan.Zero),
            EndedAt = new DateTimeOffset(2026, 2, 21, 10, 5, 0, TimeSpan.Zero),
            Success = true,
            FinalOutput = "done",
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
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = 3,
                RequestedSteps = 3,
                CompletedSteps = 3,
                RoleReplyCount = 1,
            },
        });
        var reader = new WorkflowProjectionQueryReader(
            store,
            new WorkflowExecutionReadModelMapper(),
            new InMemoryProjectionGraphStore());

        var snapshot = await reader.GetActorSnapshotAsync("actor-3");
        var timeline = await reader.ListActorTimelineAsync("actor-3", take: 2);

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("actor-3");
        snapshot.LastCommandId.Should().Be("cmd-3");
        snapshot.LastOutput.Should().Be("done");
        snapshot.TotalSteps.Should().Be(3);
        timeline.Should().HaveCount(2);
        timeline[0].Stage.Should().Be("step-3");
        timeline[1].Stage.Should().Be("step-2");
    }

    [Fact]
    public async Task QueryReader_GraphQueries_ShouldRespectDirectionFiltersAndBounds()
    {
        var store = CreateStore();
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RootActorId = "actor-graph",
            WorkflowName = "direct",
            CommandId = "cmd-graph",
            StartedAt = DateTimeOffset.UtcNow,
            Summary = new WorkflowExecutionSummary(),
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
            store,
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
        var store = CreateStore();
        var graphStore = new InMemoryProjectionGraphStore();
        var reader = new WorkflowProjectionQueryReader(
            store,
            new WorkflowExecutionReadModelMapper(),
            graphStore);

        var missing = await reader.GetActorGraphEnrichedSnapshotAsync("missing");
        missing.Should().BeNull();

        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RootActorId = "actor-enriched",
            WorkflowName = "wf-enriched",
            CommandId = "cmd-enriched",
            StartedAt = now,
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = 1,
                RequestedSteps = 1,
                CompletedSteps = 1,
                RoleReplyCount = 0,
            },
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
        var store = CreateStore();
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RootActorId = "actor-a",
            WorkflowName = "wf",
            CommandId = "cmd-a",
            StartedAt = now.AddMinutes(-2),
            Timeline =
            [
                new WorkflowExecutionTimelineEvent
                {
                    Timestamp = now.AddMinutes(-1),
                    Stage = "s-1",
                    Data = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" },
                },
            ],
            Summary = new WorkflowExecutionSummary(),
        });
        await store.UpsertAsync(new WorkflowExecutionReport
        {
            RootActorId = "actor-b",
            WorkflowName = "wf",
            CommandId = "cmd-b",
            StartedAt = now.AddMinutes(-1),
            Summary = new WorkflowExecutionSummary(),
        });

        var reader = new WorkflowProjectionQueryReader(
            store,
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
        var report = new WorkflowExecutionReport
        {
            RootActorId = "actor-map",
            WorkflowName = "wf-map",
            CommandId = "cmd-map",
            LastEventId = "evt-map",
            UpdatedAt = now,
            Success = true,
            FinalOutput = "ok",
            FinalError = "",
            Summary = new WorkflowExecutionSummary
            {
                TotalSteps = 3,
                RequestedSteps = 3,
                CompletedSteps = 3,
                RoleReplyCount = 2,
            },
        };
        var snapshot = mapper.ToActorSnapshot(report);
        snapshot.ActorId.Should().Be("actor-map");
        snapshot.WorkflowName.Should().Be("wf-map");
        snapshot.LastCommandId.Should().Be("cmd-map");
        snapshot.LastEventId.Should().Be("evt-map");
        snapshot.LastSuccess.Should().BeTrue();
        snapshot.LastOutput.Should().Be("ok");
        snapshot.TotalSteps.Should().Be(3);
        snapshot.RoleReplyCount.Should().Be(2);

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
        runEventHub.PublishedEvents[0].evt.EventCase.Should().Be(WorkflowRunEventEnvelope.EventOneofCase.RunError);
        var backpressureError = runEventHub.PublishedEvents[0].evt.RunError;
        backpressureError.Code.Should().Be(WorkflowProjectionSinkFailurePolicy.SinkBackpressureErrorCode);

        runEventHub.PublishedEvents.Clear();
        var handledCompleted = await policy.TryHandleAsync(
            lease,
            sink,
            sourceEvent,
            new EventSinkCompletedException());

        handledCompleted.Should().BeTrue();
        sinkManager.DetachCalls.Should().Be(2);
        runEventHub.PublishedEvents.Should().BeEmpty();

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
        var readModelUpdater = new RecordingReadModelUpdater();
        var ownership = new TrackingOwnershipCoordinator();
        var releaseService = new WorkflowProjectionReleaseService(
            lifecycle,
            readModelUpdater,
            ownership);
        var lease = CreateLease("actor-release", "cmd-release");

        await releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None);

        lifecycle.StoppedContexts.Should().ContainSingle();
        readModelUpdater.MarkStoppedActorIds.Should().ContainSingle().Which.Should().Be("actor-release");
        ownership.Released.Should().ContainSingle().Which.Should().Be(("actor-release", "cmd-release"));
    }

    [Fact]
    public async Task ReleaseService_WhenLiveSinkExists_ShouldSkipStopAndRelease()
    {
        var lifecycle = new RecordingLifecycleService();
        var readModelUpdater = new RecordingReadModelUpdater();
        var ownership = new TrackingOwnershipCoordinator();
        var releaseService = new WorkflowProjectionReleaseService(
            lifecycle,
            readModelUpdater,
            ownership);
        var lease = CreateLease("actor-busy", "cmd-busy");
        lease.AttachOrReplaceLiveSinkSubscription(
            new NoopRunEventSink(),
            new TrackingSubscription());

        await releaseService.ReleaseIfIdleAsync(lease, CancellationToken.None);

        lifecycle.StoppedContexts.Should().BeEmpty();
        readModelUpdater.MarkStoppedActorIds.Should().BeEmpty();
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
        listSortSelector: report => report.StartedAt);

    private static WorkflowExecutionRuntimeLease CreateLease(string actorId, string commandId) => new(
        new WorkflowExecutionProjectionContext
        {
            ProjectionId = actorId,
            RootActorId = actorId,
            CommandId = commandId,
            WorkflowName = "direct",
            Input = "hello",
            StartedAt = DateTimeOffset.UtcNow,
        });

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
        public List<(string ScopeId, string SessionId)> Acquired { get; } = [];
        public List<(string ScopeId, string SessionId)> Released { get; } = [];

        public Task AcquireAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Acquired.Add((scopeId, sessionId));
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string scopeId, string sessionId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Released.Add((scopeId, sessionId));
            return Task.CompletedTask;
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

    private sealed class RecordingReadModelUpdater : IWorkflowProjectionReadModelUpdater
    {
        public List<(string ActorId, WorkflowExecutionProjectionContext Context)> Refreshed { get; } = [];
        public List<string> MarkStoppedActorIds { get; } = [];

        public Task RefreshMetadataAsync(
            string actorId,
            WorkflowExecutionProjectionContext context,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Refreshed.Add((actorId, context));
            return Task.CompletedTask;
        }

        public Task MarkStoppedAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            MarkStoppedActorIds.Add(actorId);
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
