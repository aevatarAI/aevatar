using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Runtime;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.Projectors;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowIncrementalGraphMaterializationTests
{
    [Fact]
    public void IncrementalMaterializer_ForHundredStepReport_ShouldBuildBoundedPerEventDeltas()
    {
        var materializer = CreateMaterializer();
        var report = BuildReport(version: 101, eventId: "evt-101");
        for (var index = 0; index < 100; index++)
        {
            report.StepIndexById[$"step-{index}"] = index;
            report.Steps.Add(new WorkflowExecutionStepTrace
            {
                StepId = $"step-{index}",
                StepType = "tool_call",
                NextStepId = index == 99 ? string.Empty : $"step-{index + 1}",
            });
        }

        var nonGraph = materializer.BuildIncrementalDelta(
            report,
            StateEvent(101, "evt-101", new WorkflowCompletedEvent()),
            WorkflowProjectionKinds.ExecutionMaterialization,
            IncrementalRoute());
        var step = materializer.BuildIncrementalDelta(
            report,
            StateEvent(101, "evt-101", new StepCompletedEvent
            {
                StepId = "step-50",
                NextStepId = "step-51",
            }),
            WorkflowProjectionKinds.ExecutionMaterialization,
            IncrementalRoute());
        var topology = materializer.BuildIncrementalDelta(
            report,
            StateEvent(101, "evt-101", new WorkflowRoleActorLinkedEvent
            {
                ChildActorId = "role-actor-1",
            }),
            WorkflowProjectionKinds.ExecutionMaterialization,
            IncrementalRoute());

        nonGraph.UpsertNodes.Should().ContainSingle();
        nonGraph.UpsertEdges.Should().BeEmpty();
        nonGraph.UpsertPendingEdges.Should().BeEmpty();
        step.UpsertNodes.Should().HaveCount(2);
        step.UpsertEdges.Should().ContainSingle();
        step.UpsertPendingEdges.Should().ContainSingle();
        topology.UpsertNodes.Should().HaveCount(2);
        topology.UpsertEdges.Should().ContainSingle();

        var mutationCounts = Enumerable.Range(0, 100)
            .Select(index => materializer.BuildIncrementalDelta(
                report,
                StateEvent(101, "evt-101", new StepRequestEvent
                {
                    StepId = $"step-{index}",
                }),
                WorkflowProjectionKinds.ExecutionMaterialization,
                IncrementalRoute()))
            .Select(CountMutations)
            .ToArray();
        mutationCounts.Take(10).Should().Equal(mutationCounts.TakeLast(10));
        mutationCounts.Should().OnlyContain(count => count == 3);
    }

    [Fact]
    public async Task MixedVersionWriters_ShouldKeepIncrementalNamespaceIsolatedFromLegacyReplacement()
    {
        var store = new InMemoryProjectionGraphStore();
        var materializer = CreateMaterializer();
        var legacyWriter = new ProjectionGraphWriter<WorkflowRunInsightReportDocument>(
            store,
            new WorkflowRunInsightReportGraphMaterializer(),
            ownerIdentityResolver: ProjectionGraphOwnerIdentityResolver.Instance);
        var report = BuildReport(1, "evt-1");
        AddStep(report, new WorkflowExecutionStepTrace
        {
            StepId = "step-1",
            StepType = "assign",
        });

        await legacyWriter.UpsertAsync(report, WorkflowProjectionKinds.ExecutionMaterialization);
        var delta = materializer.BuildFullCandidateDelta(
            report,
            WorkflowProjectionKinds.ExecutionMaterialization,
            IncrementalRoute());
        (await store.ApplyDeltaAsync(delta)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.Applied);
        var route = materializer.ResolveStoreRoute(
            WorkflowProjectionKinds.ExecutionMaterialization,
            report.Id,
            IncrementalRoute());
        var beforeLegacyOverwrite = (await store.ReadOwnerSnapshotAsync(route)).Snapshot.Clone();

        var staleLegacyReport = report.Clone();
        staleLegacyReport.Steps.Clear();
        staleLegacyReport.StepIndexById.Clear();
        staleLegacyReport.StateVersion = 2;
        staleLegacyReport.LastEventId = "legacy-evt-2";
        await legacyWriter.UpsertAsync(
            staleLegacyReport,
            WorkflowProjectionKinds.ExecutionMaterialization);

        (await store.ApplyDeltaAsync(delta)).Disposition.Should()
            .Be(ProjectionGraphDeltaApplyDisposition.ExactDuplicate);
        var afterDuplicate = (await store.ReadOwnerSnapshotAsync(route)).Snapshot;
        afterDuplicate.Should().BeEquivalentTo(beforeLegacyOverwrite);
        afterDuplicate.Nodes.Should().Contain(node =>
            node.NodeId.EndsWith(":step-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncrementalMaterializer_AfterEveryRepresentativeEvent_ShouldMatchFullGoldenBusinessGraph()
    {
        var store = new InMemoryProjectionGraphStore();
        var materializer = CreateMaterializer();
        var report = BuildReport(1, "evt-1");
        var route = materializer.ResolveStoreRoute(
            WorkflowProjectionKinds.ExecutionMaterialization,
            report.Id,
            IncrementalRoute());

        (await store.ApplyDeltaAsync(materializer.BuildFullCandidateDelta(
                report,
                WorkflowProjectionKinds.ExecutionMaterialization,
                IncrementalRoute())))
            .Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
        await AssertGoldenBusinessGraphAsync(store, route, report);

        Advance(report, 2);
        AddStep(report, new WorkflowExecutionStepTrace
        {
            StepId = "step-1",
            StepType = "conditional",
            DisplayName = "Choose path",
        });
        await ApplyAsync(store, materializer, report, new StepRequestEvent { StepId = "step-1" });
        await AssertGoldenBusinessGraphAsync(store, route, report);

        Advance(report, 3);
        report.Steps[0].Success = true;
        report.Steps[0].WorkerId = "worker-1";
        report.Steps[0].NextStepId = "step-2";
        report.Steps[0].BranchKey = "true";
        await ApplyAsync(store, materializer, report, new StepCompletedEvent
        {
            StepId = "step-1",
            Success = true,
            WorkerId = "worker-1",
            NextStepId = "step-2",
            BranchKey = "true",
        });
        await AssertGoldenBusinessGraphAsync(store, route, report);

        Advance(report, 4);
        AddStep(report, new WorkflowExecutionStepTrace
        {
            StepId = "step-2",
            StepType = "assign",
        });
        await ApplyAsync(store, materializer, report, new StepRequestEvent { StepId = "step-2" });
        await AssertGoldenBusinessGraphAsync(store, route, report);

        Advance(report, 5);
        report.Topology.Add(new WorkflowExecutionTopologyEdge("actor-1", "role-actor-1"));
        await ApplyAsync(store, materializer, report, new WorkflowRoleActorLinkedEvent
        {
            ChildActorId = "role-actor-1",
        });
        await AssertGoldenBusinessGraphAsync(store, route, report);

        Advance(report, 6);
        await ApplyAsync(store, materializer, report, new WorkflowCompletedEvent());
        await AssertGoldenBusinessGraphAsync(store, route, report);
    }

    [Fact]
    public async Task Projector_WhenReportSucceededAndGraphFailed_ShouldRetryOnlyGraphPhase()
    {
        var reportStore = new RecordingDocumentStore();
        var graphWriter = new RecordingGraphWriter();
        var versionedStore = new SequencedVersionedGraphStore(
            ProjectionGraphDeltaApplyDisposition.Gap,
            ProjectionGraphDeltaApplyDisposition.Applied);
        var projector = new WorkflowRunInsightReportArtifactProjector(
            reportStore,
            reportStore,
            graphWriter,
            versionedStore,
            CreateMaterializer());
        var context = Context();
        var envelope = BuildCommittedEnvelope(1, "evt-1", new StepRequestEvent
        {
            RunId = "run-1",
            StepId = "step-1",
            StepType = "tool_call",
        });

        var first = () => projector.ProjectAsync(context, envelope).AsTask();
        await first.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Graph delta was rejected with Gap*");
        await projector.ProjectAsync(context, envelope.Clone());

        reportStore.UpsertCount.Should().Be(1);
        versionedStore.ApplyCount.Should().Be(2);
        graphWriter.UpsertCount.Should().Be(0);
    }

    [Fact]
    public async Task Projector_WhenGraphCommittedBeforeScopeWatermark_ShouldAcceptExactDuplicateOnRecovery()
    {
        var reportStore = new RecordingDocumentStore();
        var graphWriter = new RecordingGraphWriter();
        var store = new InMemoryProjectionGraphStore();
        var materializer = CreateMaterializer();
        var projector = new WorkflowRunInsightReportArtifactProjector(
            reportStore,
            reportStore,
            graphWriter,
            store,
            materializer);
        var context = Context();
        var envelope = BuildCommittedEnvelope(1, "evt-1", new StepRequestEvent
        {
            RunId = "run-1",
            StepId = "step-1",
            StepType = "tool_call",
        });

        await projector.ProjectAsync(context, envelope);
        await projector.ProjectAsync(context, envelope.Clone());

        reportStore.UpsertCount.Should().Be(1);
        graphWriter.UpsertCount.Should().Be(0);
        var route = materializer.ResolveStoreRoute(
            WorkflowProjectionKinds.ExecutionMaterialization,
            "actor-1",
            IncrementalRoute());
        var snapshot = await store.ReadOwnerSnapshotAsync(route);
        snapshot.Snapshot.Source.Should().BeEquivalentTo(new ProjectionGraphSourceCoordinate
        {
            ActorId = "actor-1",
            StateVersion = 1,
            EventId = "evt-1",
        });
    }

    [Fact]
    public async Task IncrementalMaterializer_ShouldPromoteAndRewireStableNextEdge()
    {
        var store = new InMemoryProjectionGraphStore();
        var materializer = CreateMaterializer();
        var report = BuildReport(1, "evt-1");
        AddStep(report, new WorkflowExecutionStepTrace
        {
            StepId = "step-1",
            StepType = "conditional",
            NextStepId = "step-2",
            BranchKey = "true",
        });
        await ApplyAsync(store, materializer, report, new StepCompletedEvent
        {
            StepId = "step-1",
            NextStepId = "step-2",
            BranchKey = "true",
        });
        var route = materializer.ResolveStoreRoute(
            WorkflowProjectionKinds.ExecutionMaterialization,
            report.Id,
            IncrementalRoute());
        var pending = (await store.ReadOwnerSnapshotAsync(route)).Snapshot;
        var nextEdgeId = pending.PendingEdges.Should().ContainSingle().Subject.EdgeId;

        report.StateVersion = 2;
        report.LastEventId = "evt-2";
        AddStep(report, new WorkflowExecutionStepTrace
        {
            StepId = "step-2",
            StepType = "assign",
        });
        await ApplyAsync(store, materializer, report, new StepRequestEvent { StepId = "step-2" });
        var promoted = (await store.ReadOwnerSnapshotAsync(route)).Snapshot;
        promoted.PendingEdges.Should().BeEmpty();
        promoted.Edges.Should().ContainSingle(edge =>
            edge.EdgeId == nextEdgeId && edge.ToNodeId.EndsWith(":step-2", StringComparison.Ordinal));

        report.StateVersion = 3;
        report.LastEventId = "evt-3";
        report.Steps[0].NextStepId = "step-3";
        report.Steps[0].BranchKey = "false";
        await ApplyAsync(store, materializer, report, new StepCompletedEvent
        {
            StepId = "step-1",
            NextStepId = "step-3",
            BranchKey = "false",
        });
        var rewiredPending = (await store.ReadOwnerSnapshotAsync(route)).Snapshot;
        rewiredPending.Edges.Should().NotContain(edge => edge.EdgeId == nextEdgeId);
        rewiredPending.PendingEdges.Should().ContainSingle(edge =>
            edge.EdgeId == nextEdgeId &&
            edge.ToNodeId.EndsWith(":step-3", StringComparison.Ordinal) &&
            edge.Properties["branchKey"] == "false");

        report.StateVersion = 4;
        report.LastEventId = "evt-4";
        AddStep(report, new WorkflowExecutionStepTrace
        {
            StepId = "step-3",
            StepType = "assign",
        });
        await ApplyAsync(store, materializer, report, new StepRequestEvent { StepId = "step-3" });
        var rewired = (await store.ReadOwnerSnapshotAsync(route)).Snapshot;
        rewired.PendingEdges.Should().BeEmpty();
        rewired.Edges.Should().ContainSingle(edge =>
            edge.EdgeId == nextEdgeId && edge.ToNodeId.EndsWith(":step-3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CutoverOrchestrator_ShouldRejectCandidateAfterReportAdvances_AndRebuildExactGoldenSnapshot()
    {
        var report = BuildReport(5, "evt-5");
        AddStep(report, new WorkflowExecutionStepTrace
        {
            StepId = "step-1",
            StepType = "assign",
        });
        var reader = new MutableReportReader(report);
        var store = new InMemoryProjectionGraphStore();
        var materializer = CreateMaterializer();
        var orchestrator = new WorkflowProjectionGraphCutoverOrchestrator(reader, store, materializer);
        var route = IncrementalRoute();

        var candidate = await orchestrator.BuildCandidateAsync(
            "actor-1",
            WorkflowProjectionKinds.ExecutionMaterialization,
            route);
        candidate.Should().NotBeNull();
        (await orchestrator.VerifyCandidateAsync(
                "actor-1",
                WorkflowProjectionKinds.ExecutionMaterialization,
                route,
                candidate!.Source,
                candidate.Fingerprint))
            .Should().BeTrue();

        reader.Document.StateVersion = 6;
        reader.Document.LastEventId = "evt-6";
        (await orchestrator.VerifyCandidateAsync(
                "actor-1",
                WorkflowProjectionKinds.ExecutionMaterialization,
                route,
                candidate.Source,
                candidate.Fingerprint))
            .Should().BeFalse();

        var rebuilt = await orchestrator.BuildCandidateAsync(
            "actor-1",
            WorkflowProjectionKinds.ExecutionMaterialization,
            route);
        rebuilt!.Source.StateVersion.Should().Be(6);
        rebuilt.Source.EventId.Should().Be("evt-6");
        (await orchestrator.VerifyCandidateAsync(
                "actor-1",
                WorkflowProjectionKinds.ExecutionMaterialization,
                route,
                rebuilt.Source,
                rebuilt.Fingerprint))
            .Should().BeTrue();
    }

    [Fact]
    public async Task CutoverOrchestrator_WhenLegacyReportTimestampIsMissing_ShouldBuildDeterministicCandidate()
    {
        var report = BuildReport(5, "evt-5");
        report.UpdatedAt = default;
        var orchestrator = new WorkflowProjectionGraphCutoverOrchestrator(
            new MutableReportReader(report),
            new InMemoryProjectionGraphStore(),
            CreateMaterializer());

        var candidate = await orchestrator.BuildCandidateAsync(
            report.RootActorId,
            WorkflowProjectionKinds.ExecutionMaterialization,
            IncrementalRoute());

        candidate.Should().NotBeNull();
        (await orchestrator.VerifyCandidateAsync(
                report.RootActorId,
                WorkflowProjectionKinds.ExecutionMaterialization,
                IncrementalRoute(),
                candidate!.Source,
                candidate.Fingerprint))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExplicitRollbackRoute_ShouldRequireMonotonicEpochAndFreshGoldenNamespace()
    {
        var active = IncrementalRoute();
        var rollback = active.Clone();
        rollback.PhysicalNamespace = "workflow-execution-graph.v2.rollback-1";
        rollback.RouteEpoch++;

        WorkflowProjectionGraphRoutePolicy.EnsureCanFollow(active, rollback);
        WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(rollback).Should().BeTrue();

        var staleEpoch = rollback.Clone();
        staleEpoch.RouteEpoch = active.RouteEpoch;
        var staleAct = () => WorkflowProjectionGraphRoutePolicy.EnsureCanFollow(active, staleEpoch);
        staleAct.Should().Throw<InvalidOperationException>().WithMessage("*advance the route epoch*");

        var sharedNamespace = rollback.Clone();
        sharedNamespace.PhysicalNamespace = active.PhysicalNamespace;
        var sharedAct = () => WorkflowProjectionGraphRoutePolicy.EnsureCanFollow(active, sharedNamespace);
        sharedAct.Should().Throw<InvalidOperationException>().WithMessage("*different physical namespace*");

        var report = BuildReport(7, "evt-7");
        AddStep(report, new WorkflowExecutionStepTrace
        {
            StepId = "step-rollback",
            StepType = "assign",
        });
        var store = new InMemoryProjectionGraphStore();
        var materializer = CreateMaterializer();
        var orchestrator = new WorkflowProjectionGraphCutoverOrchestrator(
            new MutableReportReader(report),
            store,
            materializer);

        var candidate = await orchestrator.BuildCandidateAsync(
            report.RootActorId,
            WorkflowProjectionKinds.ExecutionMaterialization,
            rollback);

        candidate.Should().NotBeNull();
        candidate!.Source.StateVersion.Should().Be(report.StateVersion);
        (await orchestrator.VerifyCandidateAsync(
                report.RootActorId,
                WorkflowProjectionKinds.ExecutionMaterialization,
                rollback,
                candidate.Source,
                candidate.Fingerprint))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(ProjectionMaterializationCutoverPhase.Requested, true)]
    [InlineData(ProjectionMaterializationCutoverPhase.CandidateBuilt, true)]
    [InlineData(ProjectionMaterializationCutoverPhase.GoldenVerified, true)]
    [InlineData(ProjectionMaterializationCutoverPhase.Activated, false)]
    [InlineData(ProjectionMaterializationCutoverPhase.Unspecified, false)]
    public void CutoverRecovery_WithIncrementalActiveRoute_ShouldResumeOnlyInProgressSaga(
        ProjectionMaterializationCutoverPhase phase,
        bool expected)
    {
        var activeRoute = IncrementalRoute();
        WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(activeRoute).Should().BeTrue();
        var cutover = new ProjectionMaterializationCutoverState
        {
            Phase = phase,
            CandidateRoute = activeRoute.Clone(),
        };

        WorkflowProjectionGraphRoutePolicy.HasInProgressCutover(cutover).Should().Be(expected);
    }

    [Fact]
    public void FullCandidate_WhenMutationBoundIsExceeded_ShouldReject()
    {
        var report = BuildReport(1, "evt-1");
        for (var index = 0; index < 10_000; index++)
        {
            report.Steps.Add(new WorkflowExecutionStepTrace
            {
                StepId = $"step-{index}",
                StepType = "assign",
            });
        }

        var act = () => CreateMaterializer().BuildFullCandidateDelta(
            report,
            WorkflowProjectionKinds.ExecutionMaterialization,
            IncrementalRoute());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*bounded cutover limit*");
    }

    private static async Task ApplyAsync(
        InMemoryProjectionGraphStore store,
        WorkflowRunIncrementalGraphMaterializer materializer,
        WorkflowRunInsightReportDocument report,
        IMessage payload)
    {
        var result = await store.ApplyDeltaAsync(materializer.BuildIncrementalDelta(
            report,
            StateEvent(report.StateVersion, report.LastEventId, payload),
            WorkflowProjectionKinds.ExecutionMaterialization,
            IncrementalRoute()));
        result.Disposition.Should().Be(ProjectionGraphDeltaApplyDisposition.Applied);
    }

    private static async Task AssertGoldenBusinessGraphAsync(
        InMemoryProjectionGraphStore store,
        ProjectionGraphRouteFingerprint route,
        WorkflowRunInsightReportDocument report)
    {
        var read = await store.ReadOwnerSnapshotAsync(route);
        read.Disposition.Should().Be(ProjectionGraphOwnerSnapshotReadDisposition.Found);
        read.Snapshot.Source.Should().BeEquivalentTo(new ProjectionGraphSourceCoordinate
        {
            ActorId = report.RootActorId,
            StateVersion = report.StateVersion,
            EventId = report.LastEventId,
        });

        var expected = new WorkflowRunGraphArtifactMaterializer().Materialize(report);
        read.Snapshot.Nodes
            .Select(ToBusinessNode)
            .Should().BeEquivalentTo(expected.Nodes.Select(ToBusinessNode));
        read.Snapshot.Edges
            .Select(ToBusinessEdge)
            .Should().BeEquivalentTo(expected.Edges.Select(ToBusinessEdge));
        read.Snapshot.PendingEdges.Should().OnlyContain(edge =>
            !read.Snapshot.Nodes.Any(node =>
                string.Equals(node.NodeId, edge.ToNodeId, StringComparison.Ordinal)));
    }

    private static object ToBusinessNode(ProjectionGraphNodeMutation node) => new
    {
        node.NodeId,
        node.NodeType,
        Properties = node.Properties.OrderBy(pair => pair.Key).ToArray(),
    };

    private static object ToBusinessNode(ProjectionGraphNode node) => new
    {
        node.NodeId,
        node.NodeType,
        Properties = node.Properties.OrderBy(pair => pair.Key).ToArray(),
    };

    private static object ToBusinessEdge(ProjectionGraphEdgeMutation edge) => new
    {
        edge.EdgeId,
        edge.EdgeType,
        edge.FromNodeId,
        edge.ToNodeId,
        Properties = edge.Properties.OrderBy(pair => pair.Key).ToArray(),
    };

    private static object ToBusinessEdge(ProjectionGraphEdge edge) => new
    {
        edge.EdgeId,
        edge.EdgeType,
        edge.FromNodeId,
        edge.ToNodeId,
        Properties = edge.Properties.OrderBy(pair => pair.Key).ToArray(),
    };

    private static int CountMutations(ProjectionGraphDelta delta) =>
        delta.UpsertNodes.Count +
        delta.DeleteNodeIds.Count +
        delta.UpsertEdges.Count +
        delta.DeleteEdgeIds.Count +
        delta.UpsertPendingEdges.Count +
        delta.DeletePendingEdgeIds.Count;

    private static void Advance(WorkflowRunInsightReportDocument report, long version)
    {
        report.StateVersion = version;
        report.LastEventId = $"evt-{version}";
        report.UpdatedAt = report.UpdatedAt.AddSeconds(1);
    }

    private static void AddStep(
        WorkflowRunInsightReportDocument report,
        WorkflowExecutionStepTrace step)
    {
        report.StepIndexById[step.StepId] = report.Steps.Count;
        report.Steps.Add(step);
    }

    private static WorkflowExecutionMaterializationContext Context() =>
        new()
        {
            RootActorId = "actor-1",
            ProjectionKind = WorkflowProjectionKinds.ExecutionMaterialization,
            MaterializationRoute = IncrementalRoute(),
        };

    private static WorkflowRunIncrementalGraphMaterializer CreateMaterializer() =>
        new(ProjectionGraphOwnerIdentityResolver.Instance);

    private static ProjectionMaterializationRouteFingerprint IncrementalRoute() =>
        new()
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            ContractVersion = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            PhysicalNamespace = WorkflowExecutionGraphConstants.IncrementalPhysicalNamespace,
            RouteEpoch = 2,
        };

    private static WorkflowRunInsightReportDocument BuildReport(long version, string eventId) =>
        new()
        {
            Id = "actor-1",
            RootActorId = "actor-1",
            CommandId = "cmd-1",
            WorkflowName = "workflow",
            StateVersion = version,
            LastEventId = eventId,
            UpdatedAt = DateTimeOffset.Parse("2026-08-18T08:00:00Z"),
        };

    private static StateEvent StateEvent(long version, string eventId, IMessage payload) =>
        new()
        {
            Version = version,
            EventId = eventId,
            EventData = Any.Pack(payload),
        };

    private static EventEnvelope BuildCommittedEnvelope(long version, string eventId, IMessage payload)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-18T08:00:00Z");
        return new EventEnvelope
        {
            Id = $"outer-{version}",
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("actor-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    Timestamp = Timestamp.FromDateTimeOffset(timestamp),
                    EventData = Any.Pack(payload),
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
    }

    private sealed class RecordingDocumentStore
        : IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>,
          IProjectionWriteDispatcher<WorkflowRunInsightReportDocument>
    {
        public WorkflowRunInsightReportDocument? Document { get; private set; }
        public int UpsertCount { get; private set; }

        public Task<WorkflowRunInsightReportDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Document);
        }

        public Task<ProjectionDocumentQueryResult<WorkflowRunInsightReportDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ProjectionWriteResult> UpsertAsync(
            WorkflowRunInsightReportDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Document = readModel;
            UpsertCount++;
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableReportReader(WorkflowRunInsightReportDocument document)
        : IProjectionDocumentReader<WorkflowRunInsightReportDocument, string>
    {
        public WorkflowRunInsightReportDocument Document { get; } = document;

        public Task<WorkflowRunInsightReportDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WorkflowRunInsightReportDocument?>(Document.Clone());
        }

        public Task<ProjectionDocumentQueryResult<WorkflowRunInsightReportDocument>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingGraphWriter : IProjectionGraphWriter<WorkflowRunInsightReportDocument>
    {
        public int UpsertCount { get; private set; }

        public Task UpsertAsync(
            WorkflowRunInsightReportDocument readModel,
            string projectionKind,
            CancellationToken ct = default)
        {
            UpsertCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class SequencedVersionedGraphStore(
        params ProjectionGraphDeltaApplyDisposition[] dispositions)
        : IVersionedProjectionGraphStore
    {
        private int _index;
        public int ApplyCount { get; private set; }

        public Task<ProjectionGraphDeltaApplyResult> ApplyDeltaAsync(
            ProjectionGraphDelta delta,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ApplyCount++;
            var index = Math.Min(_index++, dispositions.Length - 1);
            return Task.FromResult(new ProjectionGraphDeltaApplyResult
            {
                Disposition = dispositions[index],
            });
        }

        public Task<ProjectionGraphOwnerSnapshotReadResult> ReadOwnerSnapshotAsync(
            ProjectionGraphRouteFingerprint route,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
