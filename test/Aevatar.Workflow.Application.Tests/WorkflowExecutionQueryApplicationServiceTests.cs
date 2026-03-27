using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Queries;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowExecutionQueryApplicationServiceTests
{
    [Fact]
    public async Task QueryMethods_ShouldShortCircuit_WhenActorQueriesDisabled()
    {
        var calls = new List<string>();
        var currentStatePort = new FakeCurrentStateQueryPort(calls) { EnableActorQueryEndpoints = false };
        var artifactPort = new FakeArtifactQueryPort(calls) { EnableActorQueryEndpoints = false };
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog(["direct", "auto"]),
            currentStatePort,
            artifactPort,
            new StaticWorkflowCatalogPort(),
            new StaticWorkflowCapabilitiesPort());

        service.ActorQueryEnabled.Should().BeFalse();
        (await service.ListAgentsAsync()).Should().BeEmpty();
        (await service.GetActorSnapshotAsync("actor-1")).Should().BeNull();
        (await service.ListActorTimelineAsync("actor-1")).Should().BeEmpty();
        (await service.ListActorGraphEdgesAsync("actor-1")).Should().BeEmpty();
        var subgraph = await service.GetActorGraphSubgraphAsync("actor-1");
        subgraph.RootNodeId.Should().Be("actor-1");
        service.ListWorkflows().Should().Equal("direct", "auto");
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GraphQueries_ShouldShortCircuit_WhenActorIdBlank()
    {
        var calls = new List<string>();
        var currentStatePort = new FakeCurrentStateQueryPort(calls) { EnableActorQueryEndpoints = true };
        var artifactPort = new FakeArtifactQueryPort(calls) { EnableActorQueryEndpoints = true };
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog([]),
            currentStatePort,
            artifactPort,
            new StaticWorkflowCatalogPort(),
            new StaticWorkflowCapabilitiesPort());

        (await service.ListActorGraphEdgesAsync(" ")).Should().BeEmpty();
        (await service.GetActorGraphSubgraphAsync(" ")).RootNodeId.Should().Be(" ");
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryMethods_ShouldDelegate_WhenActorQueriesEnabled()
    {
        var snapshot = new WorkflowActorSnapshot
        {
            ActorId = "actor-1",
            WorkflowName = "direct",
        };
        var timeline = new[]
        {
            new WorkflowActorTimelineItem
            {
                StepId = "step-1",
                Stage = "completed",
            },
        };
        var edges = new[]
        {
            new WorkflowActorGraphEdge
            {
                EdgeId = "edge-1",
                FromNodeId = "actor-1",
                ToNodeId = "actor-2",
            },
        };
        var subgraph = new WorkflowActorGraphSubgraph
        {
            RootNodeId = "actor-1",
            Nodes = { new WorkflowActorGraphNode { NodeId = "actor-1" } },
            Edges = { new WorkflowActorGraphEdge { EdgeId = "edge-2" } },
        };
        var calls = new List<string>();
        var currentStatePort = new FakeCurrentStateQueryPort(calls)
        {
            EnableActorQueryEndpoints = true,
            Snapshots = [snapshot],
            SingleSnapshot = snapshot,
        };
        var artifactPort = new FakeArtifactQueryPort(calls)
        {
            EnableActorQueryEndpoints = true,
            Timeline = timeline,
            Edges = edges,
            Subgraph = subgraph,
        };
        var options = new WorkflowActorGraphQueryOptions
        {
            Direction = WorkflowActorGraphDirection.Outbound,
            EdgeTypes = ["child"],
        };
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog(["direct"]),
            currentStatePort,
            artifactPort,
            new StaticWorkflowCatalogPort(),
            new StaticWorkflowCapabilitiesPort());

        var agents = await service.ListAgentsAsync();
        var actorSnapshot = await service.GetActorSnapshotAsync("actor-1");
        var actorTimeline = await service.ListActorTimelineAsync("actor-1", 5);
        var actorEdges = await service.ListActorGraphEdgesAsync("actor-1", 7, options);
        var actorSubgraph = await service.GetActorGraphSubgraphAsync("actor-1", 3, 9, options);

        agents.Should().ContainSingle().Which.Should().Be(new WorkflowAgentSummary("actor-1", "WorkflowRunGAgent", "WorkflowRunGAgent[direct]"));
        actorSnapshot.Should().BeSameAs(snapshot);
        actorTimeline.Should().Equal(timeline);
        actorEdges.Should().Equal(edges);
        actorSubgraph.Should().BeSameAs(subgraph);
        calls.Should().ContainInOrder(
            "ListActorSnapshots:200",
            "GetActorSnapshot:actor-1",
            "ListActorTimeline:actor-1:5",
            "GetActorGraphEdges:actor-1:7:Outbound:child",
            "GetActorGraphSubgraph:actor-1:3:9:Outbound:child");
    }

    [Fact]
    public async Task ListAgentsAsync_ShouldHonorCancellation()
    {
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog([]),
            new FakeCurrentStateQueryPort([]) { EnableActorQueryEndpoints = false },
            new FakeArtifactQueryPort([]) { EnableActorQueryEndpoints = false },
            new StaticWorkflowCatalogPort(),
            new StaticWorkflowCapabilitiesPort());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await service.ListAgentsAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class StaticWorkflowDefinitionCatalog(IReadOnlyList<string> names) : IWorkflowDefinitionCatalog
    {
        public void Register(string name, string yaml) => throw new NotSupportedException();

        public WorkflowDefinitionRegistration? GetDefinition(string name) => null;

        public string? GetYaml(string name) => null;

        public IReadOnlyList<string> GetNames() => names;
    }

    private sealed class StaticWorkflowCatalogPort : IWorkflowCatalogPort
    {
        public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog() => [];

        public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName) => null;
    }

    private sealed class StaticWorkflowCapabilitiesPort : IWorkflowCapabilitiesPort
    {
        public WorkflowCapabilitiesDocument GetCapabilities() => new();
    }

    private sealed class FakeCurrentStateQueryPort(List<string> calls) : IWorkflowExecutionCurrentStateQueryPort
    {
        public bool EnableActorQueryEndpoints { get; set; }
        public IReadOnlyList<WorkflowActorSnapshot> Snapshots { get; init; } = [];
        public WorkflowActorSnapshot? SingleSnapshot { get; init; }

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            calls.Add($"GetActorSnapshot:{actorId}");
            return Task.FromResult(SingleSnapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListActorSnapshotsAsync(int take = 200, CancellationToken ct = default)
        {
            calls.Add($"ListActorSnapshots:{take}");
            return Task.FromResult(Snapshots);
        }

        public Task<WorkflowActorProjectionState?> GetActorProjectionStateAsync(string actorId, CancellationToken ct = default)
        {
            calls.Add($"GetActorProjectionState:{actorId}");
            return Task.FromResult<WorkflowActorProjectionState?>(null);
        }
    }

    private sealed class FakeArtifactQueryPort(List<string> calls) : IWorkflowExecutionArtifactQueryPort
    {
        public bool EnableActorQueryEndpoints { get; set; }
        public WorkflowRunReport? Report { get; init; }
        public IReadOnlyList<WorkflowActorTimelineItem> Timeline { get; init; } = [];
        public IReadOnlyList<WorkflowActorGraphEdge> Edges { get; init; } = [];
        public WorkflowActorGraphSubgraph Subgraph { get; init; } = new();

        public Task<WorkflowRunReport?> GetActorReportAsync(string actorId, CancellationToken ct = default)
        {
            calls.Add($"GetActorReport:{actorId}");
            return Task.FromResult(Report);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default)
        {
            calls.Add($"ListActorTimeline:{actorId}:{take}");
            return Task.FromResult(Timeline);
        }

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> GetActorGraphEdgesAsync(string actorId, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default)
        {
            calls.Add($"GetActorGraphEdges:{actorId}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(Edges);
        }

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(string actorId, int depth = 2, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default)
        {
            calls.Add($"GetActorGraphSubgraph:{actorId}:{depth}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(Subgraph);
        }
    }
}
