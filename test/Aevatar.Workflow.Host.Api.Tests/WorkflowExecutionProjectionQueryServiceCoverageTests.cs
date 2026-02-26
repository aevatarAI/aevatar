using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionProjectionQueryServiceCoverageTests
{
    [Fact]
    public async Task QueryService_WhenDisabled_ShouldReturnEmptyGraphResultsWithoutInvokingReader()
    {
        var reader = new RecordingWorkflowProjectionQueryReader();
        var service = new WorkflowExecutionProjectionQueryService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = false,
                EnableActorQueryEndpoints = true,
            },
            reader);

        service.EnableActorQueryEndpoints.Should().BeFalse();
        (await service.GetActorGraphEdgesAsync("actor-1")).Should().BeEmpty();
        (await service.GetActorGraphSubgraphAsync("actor-1")).RootNodeId.Should().Be("actor-1");
        (await service.GetActorGraphEnrichedSnapshotAsync("actor-1")).Should().BeNull();
        reader.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task QueryService_WhenActorIdIsBlank_ShouldShortCircuitGraphQueries()
    {
        var reader = new RecordingWorkflowProjectionQueryReader();
        var service = new WorkflowExecutionProjectionQueryService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            reader);

        (await service.GetActorGraphEdgesAsync("   ")).Should().BeEmpty();

        var subgraph = await service.GetActorGraphSubgraphAsync("   ");
        subgraph.RootNodeId.Should().Be("   ");
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();

        (await service.GetActorGraphEnrichedSnapshotAsync("   ")).Should().BeNull();
        reader.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task QueryService_WhenActorIdIsNull_ShouldReturnEmptyRootSubgraph()
    {
        var reader = new RecordingWorkflowProjectionQueryReader();
        var service = new WorkflowExecutionProjectionQueryService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            reader);

        var subgraph = await service.GetActorGraphSubgraphAsync(null!);

        subgraph.RootNodeId.Should().BeEmpty();
        subgraph.Nodes.Should().BeEmpty();
        subgraph.Edges.Should().BeEmpty();
        reader.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task QueryService_WhenEnabled_ShouldForwardGraphOptionsToReader()
    {
        var now = DateTimeOffset.UtcNow;
        var reader = new RecordingWorkflowProjectionQueryReader
        {
            GraphEdgesResult =
            [
                new WorkflowActorGraphEdge
                {
                    EdgeId = "edge-1",
                    FromNodeId = "actor-1",
                    ToNodeId = "actor-2",
                    EdgeType = "CHILD_OF",
                    UpdatedAt = now,
                },
            ],
            GraphSubgraphResult = new WorkflowActorGraphSubgraph
            {
                RootNodeId = "actor-1",
                Nodes =
                [
                    new WorkflowActorGraphNode
                    {
                        NodeId = "actor-1",
                        NodeType = "Actor",
                    },
                ],
            },
            GraphEnrichedResult = new WorkflowActorGraphEnrichedSnapshot
            {
                Snapshot = new WorkflowActorSnapshot
                {
                    ActorId = "actor-1",
                    WorkflowName = "wf",
                },
                Subgraph = new WorkflowActorGraphSubgraph
                {
                    RootNodeId = "actor-1",
                },
            },
        };
        var service = new WorkflowExecutionProjectionQueryService(
            new WorkflowExecutionProjectionOptions
            {
                Enabled = true,
                EnableActorQueryEndpoints = true,
            },
            reader);

        var options = new WorkflowActorGraphQueryOptions
        {
            Direction = WorkflowActorGraphDirection.Inbound,
            EdgeTypes = ["CHILD_OF"],
        };

        var edges = await service.GetActorGraphEdgesAsync("actor-1", take: 7, options: options);
        var subgraph = await service.GetActorGraphSubgraphAsync("actor-1", depth: 3, take: 9, options: options);
        var enriched = await service.GetActorGraphEnrichedSnapshotAsync("actor-1", depth: 4, take: 11, options: options);

        edges.Should().ContainSingle(x => x.EdgeId == "edge-1");
        subgraph.RootNodeId.Should().Be("actor-1");
        enriched.Should().NotBeNull();
        enriched!.Snapshot.ActorId.Should().Be("actor-1");

        reader.LastGraphEdgesCall.Should().Be(("actor-1", 7, options));
        reader.LastGraphSubgraphCall.Should().Be(("actor-1", 3, 9, options));
        reader.LastGraphEnrichedCall.Should().Be(("actor-1", 4, 11, options));
    }

    private sealed class RecordingWorkflowProjectionQueryReader : IWorkflowProjectionQueryReader
    {
        public int TotalCalls { get; private set; }
        public (string ActorId, int Take, WorkflowActorGraphQueryOptions? Options)? LastGraphEdgesCall { get; private set; }
        public (string ActorId, int Depth, int Take, WorkflowActorGraphQueryOptions? Options)? LastGraphSubgraphCall { get; private set; }
        public (string ActorId, int Depth, int Take, WorkflowActorGraphQueryOptions? Options)? LastGraphEnrichedCall { get; private set; }

        public IReadOnlyList<WorkflowActorGraphEdge> GraphEdgesResult { get; init; } = [];
        public WorkflowActorGraphSubgraph GraphSubgraphResult { get; init; } = new();
        public WorkflowActorGraphEnrichedSnapshot? GraphEnrichedResult { get; init; }

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            TotalCalls++;
            return Task.FromResult<WorkflowActorSnapshot?>(null);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(int take = 200, CancellationToken ct = default)
        {
            _ = take;
            ct.ThrowIfCancellationRequested();
            TotalCalls++;
            return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default)
        {
            _ = actorId;
            _ = take;
            ct.ThrowIfCancellationRequested();
            TotalCalls++;
            return Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>([]);
        }

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TotalCalls++;
            LastGraphEdgesCall = (actorId, take, options);
            return Task.FromResult(GraphEdgesResult);
        }

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TotalCalls++;
            LastGraphSubgraphCall = (actorId, depth, take, options);
            return Task.FromResult(GraphSubgraphResult);
        }

        public Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TotalCalls++;
            LastGraphEnrichedCall = (actorId, depth, take, options);
            return Task.FromResult(GraphEnrichedResult);
        }
    }
}
