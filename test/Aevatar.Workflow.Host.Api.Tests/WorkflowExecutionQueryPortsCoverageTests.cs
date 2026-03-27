using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionQueryPortsCoverageTests
{
    [Theory]
    [InlineData("running", WorkflowRunCompletionStatus.Running)]
    [InlineData("completed", WorkflowRunCompletionStatus.Completed)]
    [InlineData("failed", WorkflowRunCompletionStatus.Failed)]
    [InlineData("stopped", WorkflowRunCompletionStatus.Stopped)]
    [InlineData("not_found", WorkflowRunCompletionStatus.NotFound)]
    [InlineData("disabled", WorkflowRunCompletionStatus.Disabled)]
    [InlineData("unknown", WorkflowRunCompletionStatus.Unknown)]
    public void WorkflowExecutionReadModelMapper_ShouldMapCurrentStateStatuses(
        string status,
        WorkflowRunCompletionStatus expected)
    {
        var mapper = new WorkflowExecutionReadModelMapper();
        var snapshot = mapper.ToActorSnapshot(new WorkflowExecutionCurrentStateDocument
        {
            Id = "actor-1",
            RootActorId = "actor-1",
            CommandId = "cmd-1",
            Status = status,
            FinalOutput = "done",
            FinalError = "err",
            UpdatedAt = DateTimeOffset.Parse("2026-03-17T08:00:00+00:00"),
        });

        snapshot.CompletionStatus.Should().Be(expected);
        snapshot.LastOutput.Should().Be("done");
        snapshot.LastError.Should().Be("err");
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenDisabled_ShouldReturnEmptyGraphResultsWithoutTouchingStores()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = false,
            EnableActorQueryEndpoints = true,
        });

        harness.ArtifactPort.EnableActorQueryEndpoints.Should().BeFalse();
        (await harness.ArtifactPort.GetActorGraphEdgesAsync("actor-1")).Should().BeEmpty();
        (await harness.ArtifactPort.GetActorGraphSubgraphAsync("actor-1")).RootNodeId.Should().Be("actor-1");
        harness.CurrentStateReader.GetCalls.Should().Be(0);
        harness.TimelineReader.GetCalls.Should().Be(0);
        harness.GraphStore.GetNeighborsCalls.Should().Be(0);
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenActorIdIsBlank_ShouldShortCircuitGraphQueries()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = true,
        });

        (await harness.ArtifactPort.GetActorGraphEdgesAsync("   ")).Should().BeEmpty();

        var subgraph = await harness.ArtifactPort.GetActorGraphSubgraphAsync("   ");
        subgraph.RootNodeId.Should().BeEmpty();
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();

        harness.GraphStore.GetNeighborsCalls.Should().Be(0);
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenActorIdIsNull_ShouldReturnEmptyRootSubgraph()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = true,
        });

        var subgraph = await harness.ArtifactPort.GetActorGraphSubgraphAsync(null!);

        subgraph.RootNodeId.Should().BeEmpty();
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task CurrentStateQueryPort_WhenEnabled_ShouldReadAndMapCurrentStateDocuments()
    {
        var now = DateTimeOffset.UtcNow;
        var harness = CreateHarness(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            currentStateReader: new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>
            {
                Item = new WorkflowExecutionCurrentStateDocument
                {
                    Id = "actor-1",
                    RootActorId = "actor-1",
                    WorkflowName = "wf",
                    StateVersion = 12,
                    LastEventId = "evt-12",
                    UpdatedAt = now,
                },
                Items =
                [
                    new WorkflowExecutionCurrentStateDocument
                    {
                        Id = "actor-1",
                        RootActorId = "actor-1",
                        WorkflowName = "wf",
                        StateVersion = 12,
                        LastEventId = "evt-12",
                        UpdatedAt = now,
                    },
                ],
            });

        var snapshot = await harness.CurrentStatePort.GetActorSnapshotAsync("actor-1");
        var snapshots = await harness.CurrentStatePort.ListActorSnapshotsAsync(5);
        var projectionState = await harness.CurrentStatePort.GetActorProjectionStateAsync("actor-1");

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("actor-1");
        snapshot.StateVersion.Should().Be(12);
        snapshots.Should().ContainSingle();
        projectionState.Should().NotBeNull();
        projectionState!.ActorId.Should().Be("actor-1");
        harness.CurrentStateReader.GetCalls.Should().Be(2);
        harness.CurrentStateReader.QueryCalls.Should().Be(1);
    }

    [Fact]
    public async Task CurrentStateQueryPort_WhenDisabledBlankOrMissing_ShouldShortCircuit()
    {
        var disabled = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = false,
        });
        disabled.CurrentStatePort.EnableActorQueryEndpoints.Should().BeFalse();
        (await disabled.CurrentStatePort.GetActorSnapshotAsync("actor-1")).Should().BeNull();
        (await disabled.CurrentStatePort.ListActorSnapshotsAsync()).Should().BeEmpty();
        (await disabled.CurrentStatePort.GetActorProjectionStateAsync("actor-1")).Should().BeNull();
        disabled.CurrentStateReader.GetCalls.Should().Be(0);
        disabled.CurrentStateReader.QueryCalls.Should().Be(0);

        var missing = CreateHarness(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            currentStateReader: new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>());
        (await missing.CurrentStatePort.GetActorSnapshotAsync("   ")).Should().BeNull();
        (await missing.CurrentStatePort.GetActorProjectionStateAsync("actor-404")).Should().BeNull();
        missing.CurrentStateReader.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task ArtifactQueryPort_ListActorTimelineAsync_ShouldOrderClampAndMapEventData()
    {
        var harness = CreateHarness(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            timelineReader: new RecordingDocumentReader<WorkflowRunTimelineDocument>
            {
                Item = new WorkflowRunTimelineDocument
                {
                    Id = "actor-1",
                    RootActorId = "actor-1",
                    Timeline =
                    {
                        new WorkflowExecutionTimelineEvent
                        {
                            Timestamp = DateTimeOffset.Parse("2026-03-17T08:01:00+00:00"),
                            Stage = "older",
                            Message = "msg-1",
                            EventType = "type-1",
                            Data = { ["k1"] = "v1" },
                        },
                        new WorkflowExecutionTimelineEvent
                        {
                            Timestamp = DateTimeOffset.Parse("2026-03-17T08:03:00+00:00"),
                            Stage = "newer",
                            Message = "msg-2",
                            EventType = "type-2",
                            Data = { ["k2"] = "v2" },
                        },
                        new WorkflowExecutionTimelineEvent
                        {
                            Timestamp = DateTimeOffset.Parse("2026-03-17T08:02:00+00:00"),
                            Stage = "middle",
                            Message = "msg-3",
                            EventType = "type-3",
                        },
                    },
                },
            });

        var items = await harness.ArtifactPort.ListActorTimelineAsync("actor-1", take: 2);

        items.Select(x => x.Stage).Should().Equal("newer", "middle");
        items[0].Data.Should().Contain("k2", "v2");
        harness.TimelineReader.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task ArtifactQueryPort_ListActorTimelineAsync_ShouldShortCircuitWhenDisabledBlankOrMissing()
    {
        var disabled = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = false,
            EnableActorQueryEndpoints = true,
        });
        (await disabled.ArtifactPort.ListActorTimelineAsync("actor-1")).Should().BeEmpty();
        disabled.TimelineReader.GetCalls.Should().Be(0);

        var enabled = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = true,
        });
        (await enabled.ArtifactPort.ListActorTimelineAsync("   ")).Should().BeEmpty();
        (await enabled.ArtifactPort.ListActorTimelineAsync("actor-404")).Should().BeEmpty();
        enabled.TimelineReader.GetCalls.Should().Be(1);
    }

    [Fact]
    public async Task ArtifactQueryPort_WhenEnabled_ShouldForwardGraphOptionsToGraphStore()
    {
        var now = DateTimeOffset.UtcNow;
        var harness = CreateHarness(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            graphStore: new RecordingProjectionGraphStore
            {
                GraphEdgesResult =
                [
                    new ProjectionGraphEdge
                    {
                        Scope = WorkflowExecutionGraphConstants.Scope,
                        EdgeId = "edge-1",
                        FromNodeId = "actor-1",
                        ToNodeId = "actor-2",
                        EdgeType = "CHILD_OF",
                        UpdatedAt = now,
                    },
                ],
                GraphSubgraphResult = new ProjectionGraphSubgraph
                {
                    Nodes =
                    [
                        new ProjectionGraphNode
                        {
                            Scope = WorkflowExecutionGraphConstants.Scope,
                            NodeId = "actor-1",
                            NodeType = "Actor",
                        },
                    ],
                },
            },
            currentStateReader: new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>
            {
                Item = new WorkflowExecutionCurrentStateDocument
                {
                    Id = "actor-1",
                    RootActorId = "actor-1",
                    StateVersion = 12,
                    LastEventId = "evt-12",
                    UpdatedAt = now,
                    WorkflowName = "wf",
                },
            });

        var options = new WorkflowActorGraphQueryOptions
        {
            Direction = WorkflowActorGraphDirection.Inbound,
            EdgeTypes = ["CHILD_OF"],
        };

        var edges = await harness.ArtifactPort.GetActorGraphEdgesAsync("actor-1", take: 7, options: options);
        var subgraph = await harness.ArtifactPort.GetActorGraphSubgraphAsync("actor-1", depth: 4, take: 11, options: options);

        edges.Should().ContainSingle(x => x.EdgeId == "edge-1");
        subgraph.RootNodeId.Should().Be("actor-1");

        harness.GraphStore.LastGraphEdgesQuery.Should().NotBeNull();
        harness.GraphStore.LastGraphEdgesQuery!.RootNodeId.Should().Be("actor-1");
        harness.GraphStore.LastGraphEdgesQuery.Take.Should().Be(7);
        harness.GraphStore.LastGraphEdgesQuery.Direction.Should().Be(ProjectionGraphDirection.Inbound);
        harness.GraphStore.LastGraphEdgesQuery.EdgeTypes.Should().Equal("CHILD_OF");

        harness.GraphStore.LastSubgraphQuery.Should().NotBeNull();
        harness.GraphStore.LastSubgraphQuery!.RootNodeId.Should().Be("actor-1");
        harness.GraphStore.LastSubgraphQuery.Depth.Should().Be(4);
        harness.GraphStore.LastSubgraphQuery.Take.Should().Be(11);
    }

    [Fact]
    public async Task ArtifactQueryPort_ShouldNormalizeBlankEdgeTypes_AndDefaultDirectionToBoth()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = true,
        });

        var options = new WorkflowActorGraphQueryOptions
        {
            Direction = (WorkflowActorGraphDirection)99,
            EdgeTypes = [" CHILD_OF ", "", "CHILD_OF", "  ", "OWNS"],
        };

        await harness.ArtifactPort.GetActorGraphEdgesAsync("actor-1", take: 0, options: options);
        await harness.ArtifactPort.GetActorGraphSubgraphAsync("actor-1", depth: 99, take: 5001, options: options);

        harness.GraphStore.LastGraphEdgesQuery.Should().NotBeNull();
        harness.GraphStore.LastGraphEdgesQuery!.Direction.Should().Be(ProjectionGraphDirection.Both);
        harness.GraphStore.LastGraphEdgesQuery.EdgeTypes.Should().Equal("CHILD_OF", "OWNS");
        harness.GraphStore.LastGraphEdgesQuery.Take.Should().Be(1);

        harness.GraphStore.LastSubgraphQuery.Should().NotBeNull();
        harness.GraphStore.LastSubgraphQuery!.Direction.Should().Be(ProjectionGraphDirection.Both);
        harness.GraphStore.LastSubgraphQuery.Depth.Should().Be(8);
        harness.GraphStore.LastSubgraphQuery.Take.Should().Be(2000);
    }

    private static QueryPortHarness CreateHarness(
        WorkflowExecutionProjectionOptions options,
        RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>? currentStateReader = null,
        RecordingDocumentReader<WorkflowRunInsightReportDocument>? reportReader = null,
        RecordingDocumentReader<WorkflowRunTimelineDocument>? timelineReader = null,
        RecordingProjectionGraphStore? graphStore = null)
    {
        currentStateReader ??= new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>();
        reportReader ??= new RecordingDocumentReader<WorkflowRunInsightReportDocument>();
        timelineReader ??= new RecordingDocumentReader<WorkflowRunTimelineDocument>();
        graphStore ??= new RecordingProjectionGraphStore();
        return new QueryPortHarness(
            new WorkflowExecutionCurrentStateQueryPort(
                currentStateReader,
                reportReader,
                new WorkflowExecutionReadModelMapper(),
                options),
            new WorkflowExecutionArtifactQueryPort(
                reportReader,
                timelineReader,
                new WorkflowExecutionReadModelMapper(),
                graphStore,
                options),
            currentStateReader,
            reportReader,
            timelineReader,
            graphStore);
    }

    private sealed record QueryPortHarness(
        IWorkflowExecutionCurrentStateQueryPort CurrentStatePort,
        IWorkflowExecutionArtifactQueryPort ArtifactPort,
        RecordingDocumentReader<WorkflowExecutionCurrentStateDocument> CurrentStateReader,
        RecordingDocumentReader<WorkflowRunInsightReportDocument> ReportReader,
        RecordingDocumentReader<WorkflowRunTimelineDocument> TimelineReader,
        RecordingProjectionGraphStore GraphStore);

    private sealed class RecordingDocumentReader<TReadModel> : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public int GetCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public TReadModel? Item { get; init; }
        public IReadOnlyList<TReadModel> Items { get; init; } = [];

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            _ = key;
            ct.ThrowIfCancellationRequested();
            GetCalls++;
            return Task.FromResult(Item);
        }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            QueryCalls++;
            return Task.FromResult(new ProjectionDocumentQueryResult<TReadModel>
            {
                Items = Items,
            });
        }
    }

    private sealed class RecordingProjectionGraphStore : IProjectionGraphStore
    {
        public int GetNeighborsCalls { get; private set; }
        public int GetSubgraphCalls { get; private set; }
        public ProjectionGraphQuery? LastGraphEdgesQuery { get; private set; }
        public ProjectionGraphQuery? LastSubgraphQuery { get; private set; }
        public IReadOnlyList<ProjectionGraphEdge> GraphEdgesResult { get; init; } = [];
        public ProjectionGraphSubgraph GraphSubgraphResult { get; init; } = new();

        public Task ReplaceOwnerGraphAsync(ProjectionOwnedGraph graph, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertNodeAsync(ProjectionGraphNode node, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertEdgeAsync(ProjectionGraphEdge edge, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteNodeAsync(string scope, string nodeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteEdgeAsync(string scope, string edgeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectionGraphNode>> ListNodesByOwnerAsync(string scope, string ownerId, int skip = 0, int take = 5000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectionGraphEdge>> ListEdgesByOwnerAsync(string scope, string ownerId, int skip = 0, int take = 5000, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectionGraphEdge>> GetNeighborsAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetNeighborsCalls++;
            LastGraphEdgesQuery = query;
            return Task.FromResult(GraphEdgesResult);
        }

        public Task<ProjectionGraphSubgraph> GetSubgraphAsync(
            ProjectionGraphQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetSubgraphCalls++;
            LastSubgraphQuery = query;
            return Task.FromResult(GraphSubgraphResult);
        }
    }
}
