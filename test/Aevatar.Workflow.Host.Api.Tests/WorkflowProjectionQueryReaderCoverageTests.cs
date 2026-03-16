using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowProjectionQueryReaderCoverageTests
{
    [Fact]
    public async Task QueryPort_WhenDisabled_ShouldReturnEmptyGraphResultsWithoutTouchingStores()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = false,
            EnableActorQueryEndpoints = true,
        });

        harness.Port.EnableActorQueryEndpoints.Should().BeFalse();
        (await harness.Port.GetActorGraphEdgesAsync("actor-1")).Should().BeEmpty();
        (await harness.Port.GetActorGraphSubgraphAsync("actor-1")).RootNodeId.Should().Be("actor-1");
        (await harness.Port.GetActorGraphEnrichedSnapshotAsync("actor-1")).Should().BeNull();
        harness.CurrentStateReader.GetCalls.Should().Be(0);
        harness.TimelineReader.GetCalls.Should().Be(0);
        harness.GraphStore.GetNeighborsCalls.Should().Be(0);
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task QueryPort_WhenActorIdIsBlank_ShouldShortCircuitGraphQueries()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = true,
        });

        (await harness.Port.GetActorGraphEdgesAsync("   ")).Should().BeEmpty();

        var subgraph = await harness.Port.GetActorGraphSubgraphAsync("   ");
        subgraph.RootNodeId.Should().BeEmpty();
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();

        (await harness.Port.GetActorGraphEnrichedSnapshotAsync("   ")).Should().BeNull();
        harness.GraphStore.GetNeighborsCalls.Should().Be(0);
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task QueryPort_WhenActorIdIsNull_ShouldReturnEmptyRootSubgraph()
    {
        var harness = CreateHarness(new WorkflowExecutionProjectionOptions
        {
            Enabled = true,
            EnableActorQueryEndpoints = true,
        });

        var subgraph = await harness.Port.GetActorGraphSubgraphAsync(null!);

        subgraph.RootNodeId.Should().BeEmpty();
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();
        harness.GraphStore.GetSubgraphCalls.Should().Be(0);
    }

    [Fact]
    public async Task QueryPort_WhenEnabled_ShouldForwardGraphOptionsToGraphStore()
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

        var edges = await harness.Port.GetActorGraphEdgesAsync("actor-1", take: 7, options: options);
        var subgraph = await harness.Port.GetActorGraphSubgraphAsync("actor-1", depth: 3, take: 9, options: options);
        var enriched = await harness.Port.GetActorGraphEnrichedSnapshotAsync("actor-1", depth: 4, take: 11, options: options);

        edges.Should().ContainSingle(x => x.EdgeId == "edge-1");
        subgraph.RootNodeId.Should().Be("actor-1");
        enriched.Should().NotBeNull();
        enriched!.Snapshot.ActorId.Should().Be("actor-1");

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

    private static QueryPortHarness CreateHarness(
        WorkflowExecutionProjectionOptions options,
        RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>? currentStateReader = null,
        RecordingDocumentReader<WorkflowRunTimelineDocument>? timelineReader = null,
        RecordingProjectionGraphStore? graphStore = null)
    {
        currentStateReader ??= new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>();
        timelineReader ??= new RecordingDocumentReader<WorkflowRunTimelineDocument>();
        graphStore ??= new RecordingProjectionGraphStore();
        return new QueryPortHarness(
            new WorkflowProjectionQueryReader(
                currentStateReader,
                timelineReader,
                new WorkflowExecutionReadModelMapper(),
                graphStore,
                options),
            currentStateReader,
            timelineReader,
            graphStore);
    }

    private sealed record QueryPortHarness(
        WorkflowProjectionQueryReader Port,
        RecordingDocumentReader<WorkflowExecutionCurrentStateDocument> CurrentStateReader,
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
