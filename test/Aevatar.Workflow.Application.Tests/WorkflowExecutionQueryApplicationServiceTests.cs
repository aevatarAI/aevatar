using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Workflows;
using Aevatar.Workflow.Application.Queries;
using Aevatar.Workflow.Application.Workflows;
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
        (await service.ListWorkflowRunTimelineExportAsync("actor-1")).Should().BeEmpty();
        (await service.ListWorkflowRunGraphExportEdgesAsync("actor-1")).Should().BeEmpty();
        var subgraph = await service.GetWorkflowRunGraphExportSubgraphAsync("actor-1");
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

        (await service.ListWorkflowRunGraphExportEdgesAsync(" ")).Should().BeEmpty();
        (await service.GetWorkflowRunGraphExportSubgraphAsync(" ")).RootNodeId.Should().Be(" ");
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
            new WorkflowRunTimelineExportItem
            {
                StepId = "step-1",
                Stage = "completed",
            },
        };
        var edges = new[]
        {
            new WorkflowRunGraphExportEdge
            {
                EdgeId = "edge-1",
                FromNodeId = "actor-1",
                ToNodeId = "actor-2",
            },
        };
        var subgraph = new WorkflowRunGraphExportSubgraph
        {
            RootNodeId = "actor-1",
            Nodes = { new WorkflowRunGraphExportNode { NodeId = "actor-1" } },
            Edges = { new WorkflowRunGraphExportEdge { EdgeId = "edge-2" } },
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
        var options = new WorkflowRunGraphExportQueryOptions
        {
            Direction = WorkflowRunGraphExportDirection.Outbound,
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
        var actorTimeline = await service.ListWorkflowRunTimelineExportAsync("actor-1", 5);
        var actorEdges = await service.ListWorkflowRunGraphExportEdgesAsync("actor-1", 7, options);
        var actorSubgraph = await service.GetWorkflowRunGraphExportSubgraphAsync("actor-1", 3, 9, options);

        agents.Should().ContainSingle().Which.Should().Be(new WorkflowAgentSummary("actor-1", "WorkflowRunGAgent", "WorkflowRunGAgent[direct]"));
        actorSnapshot.Should().BeSameAs(snapshot);
        actorTimeline.Should().Equal(timeline);
        actorEdges.Should().Equal(edges);
        actorSubgraph.Should().BeSameAs(subgraph);
        calls.Should().ContainInOrder(
            "ListActorSnapshots:200",
            "GetActorSnapshot:actor-1",
            "ListWorkflowRunTimelineExport:actor-1:5",
            "GetWorkflowRunGraphExportEdges:actor-1:7:Outbound:child",
            "GetWorkflowRunGraphExportSubgraph:actor-1:3:9:Outbound:child");
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

    [Fact]
    public async Task CatalogAndCapabilitiesQueries_ShouldDelegateAsyncAndPassCancellationToken()
    {
        var catalogPort = new RecordingWorkflowCatalogPort
        {
            Catalog =
            [
                new WorkflowCatalogItem
                {
                    Name = "direct",
                },
            ],
            Detail = new WorkflowCatalogItemDetail
            {
                Catalog = new WorkflowCatalogItem
                {
                    Name = "direct",
                },
            },
        };
        var capabilitiesPort = new RecordingWorkflowCapabilitiesPort
        {
            Capabilities = new WorkflowCapabilitiesDocument
            {
                SchemaVersion = "capabilities.v1",
            },
        };
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog([]),
            new FakeCurrentStateQueryPort([]),
            new FakeArtifactQueryPort([]),
            catalogPort,
            capabilitiesPort);
        using var cts = new CancellationTokenSource();

        var catalog = await service.ListWorkflowCatalogAsync(cts.Token);
        var detail = await service.GetWorkflowDetailAsync("direct", cts.Token);
        var blankDetail = await service.GetWorkflowDetailAsync("   ", cts.Token);
        var capabilities = await service.GetCapabilitiesAsync(cts.Token);

        catalog.Should().ContainSingle(item => item.Name == "direct");
        detail.Should().NotBeNull();
        blankDetail.Should().BeNull();
        capabilities.SchemaVersion.Should().Be("capabilities.v1");
        catalogPort.Calls.Should().Equal("ListWorkflowCatalog", "GetWorkflowDetail:direct");
        capabilitiesPort.Calls.Should().Equal("GetCapabilities");
        catalogPort.CancellationTokens.Should().OnlyContain(token => token == cts.Token);
        capabilitiesPort.CancellationTokens.Should().OnlyContain(token => token == cts.Token);
    }

    [Fact]
    public async Task RegistryBackedWorkflowCatalogPort_ShouldExposeStartupCatalogThroughAsyncQueryMethods()
    {
        var registry = new WorkflowDefinitionCatalog();
        registry.Register("beta", """
            name: beta
            description: Beta workflow.
            steps:
              - id: reply
                type: llm_call
            """);
        registry.Register("alpha", """
            name: alpha
            description: Alpha workflow.
            steps:
              - id: reply
                type: llm_call
            """);
        var port = new RegistryBackedWorkflowCatalogPort(registry);

        var catalog = await port.ListWorkflowCatalogAsync();
        var detail = await port.GetWorkflowDetailAsync(" alpha ");
        var blankDetail = await port.GetWorkflowDetailAsync("   ");
        var missingDetail = await port.GetWorkflowDetailAsync("missing");
        var capabilities = await port.GetCapabilitiesAsync();

        catalog.Select(item => item.Name).Should().Equal("alpha", "beta");
        catalog.Should().OnlyContain(item =>
            item.Source == "builtin" &&
            item.SourceLabel == "Built-in" &&
            item.Group == "starter-workflows" &&
            item.GroupLabel == "Starter Workflows" &&
            item.ShowInLibrary);
        detail.Should().NotBeNull();
        detail!.Catalog.Name.Should().Be("alpha");
        detail.Yaml.Should().Contain("name: alpha");
        blankDetail.Should().BeNull();
        missingDetail.Should().BeNull();
        capabilities.SchemaVersion.Should().Be("capabilities.v1");
        capabilities.Workflows.Select(workflow => workflow.Name).Should().Equal("alpha", "beta");
        capabilities.Workflows.Should().OnlyContain(workflow => workflow.Source == "builtin");
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
        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowCatalogItem>>([]);

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default) =>
            Task.FromResult<WorkflowCatalogItemDetail?>(null);
    }

    private sealed class StaticWorkflowCapabilitiesPort : IWorkflowCapabilitiesPort
    {
        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCapabilitiesDocument());
    }

    private sealed class RecordingWorkflowCatalogPort : IWorkflowCatalogPort
    {
        public IReadOnlyList<WorkflowCatalogItem> Catalog { get; init; } = [];
        public WorkflowCatalogItemDetail? Detail { get; init; }
        public List<string> Calls { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default)
        {
            Calls.Add("ListWorkflowCatalog");
            CancellationTokens.Add(ct);
            return Task.FromResult(Catalog);
        }

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowDetail:{workflowName}");
            CancellationTokens.Add(ct);
            return Task.FromResult(Detail);
        }
    }

    private sealed class RecordingWorkflowCapabilitiesPort : IWorkflowCapabilitiesPort
    {
        public WorkflowCapabilitiesDocument Capabilities { get; init; } = new();
        public List<string> Calls { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default)
        {
            Calls.Add("GetCapabilities");
            CancellationTokens.Add(ct);
            return Task.FromResult(Capabilities);
        }
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
        public IReadOnlyList<WorkflowRunTimelineExportItem> Timeline { get; init; } = [];
        public IReadOnlyList<WorkflowRunGraphExportEdge> Edges { get; init; } = [];
        public WorkflowRunGraphExportSubgraph Subgraph { get; init; } = new();

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string actorId, CancellationToken ct = default)
        {
            calls.Add($"GetWorkflowRunReportArtifact:{actorId}");
            return Task.FromResult(Report);
        }

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(string actorId, int take = 200, CancellationToken ct = default)
        {
            calls.Add($"ListWorkflowRunTimelineExport:{actorId}:{take}");
            return Task.FromResult(Timeline);
        }

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> GetWorkflowRunGraphExportEdgesAsync(string actorId, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default)
        {
            calls.Add($"GetWorkflowRunGraphExportEdges:{actorId}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(Edges);
        }

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(string actorId, int depth = 2, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default)
        {
            calls.Add($"GetWorkflowRunGraphExportSubgraph:{actorId}:{depth}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(Subgraph);
        }
    }
}
