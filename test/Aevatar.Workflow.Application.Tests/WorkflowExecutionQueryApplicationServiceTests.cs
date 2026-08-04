using Aevatar.Workflow.Abstractions;
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
    public async Task QueryMethods_ShouldShortCircuit_WhenWorkflowActorCurrentStateQueryDisabled()
    {
        var calls = new List<string>();
        var currentStatePort = new FakeCurrentStateQueryPort(calls) { WorkflowActorCurrentStateQueryEnabled = false };
        var artifactPort = new FakeArtifactQueryPort(calls) { WorkflowArtifactQueryEnabled = false };
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog(["direct", "auto"]),
            currentStatePort,
            artifactPort,
            new StaticWorkflowCatalogPort(),
            new StaticWorkflowCapabilitiesPort());

        service.WorkflowActorCurrentStateQueryEnabled.Should().BeFalse();
        (await service.ListAgentsAsync()).Should().BeEmpty();
        (await service.GetWorkflowActorCurrentStateAsync("run-1")).Should().BeNull();
        (await service.ListWorkflowRunTimelineExportAsync("run-1")).Should().BeEmpty();
        (await service.ListWorkflowRunGraphExportEdgesAsync("run-1")).Should().BeEmpty();
        var subgraph = await service.GetWorkflowRunGraphExportSubgraphAsync("run-1");
        subgraph.RootNodeId.Should().Be("run-1");
        service.ListWorkflows().Should().Equal("direct", "auto");
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GraphQueries_ShouldShortCircuit_WhenActorIdBlank()
    {
        var calls = new List<string>();
        var currentStatePort = new FakeCurrentStateQueryPort(calls) { WorkflowActorCurrentStateQueryEnabled = true };
        var artifactPort = new FakeArtifactQueryPort(calls) { WorkflowArtifactQueryEnabled = true };
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
    public async Task QueryMethods_ShouldDelegate_WhenWorkflowActorCurrentStateQueryEnabled()
    {
        var snapshot = new WorkflowActorSnapshot
        {
            ActorId = "run-1",
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
                FromNodeId = "run-1",
                ToNodeId = "run-2",
            },
        };
        var subgraph = new WorkflowRunGraphExportSubgraph
        {
            RootNodeId = "run-1",
            Nodes = { new WorkflowRunGraphExportNode { NodeId = "run-1" } },
            Edges = { new WorkflowRunGraphExportEdge { EdgeId = "edge-2" } },
        };
        var calls = new List<string>();
        var currentStatePort = new FakeCurrentStateQueryPort(calls)
        {
            WorkflowActorCurrentStateQueryEnabled = true,
            Snapshots = [snapshot],
            SingleSnapshot = snapshot,
        };
        var artifactPort = new FakeArtifactQueryPort(calls)
        {
            WorkflowArtifactQueryEnabled = true,
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
        var currentState = await service.GetWorkflowActorCurrentStateAsync("run-1");
        var queriedTimeline = await service.ListWorkflowRunTimelineExportAsync("run-1", 5);
        var queriedEdges = await service.ListWorkflowRunGraphExportEdgesAsync("run-1", 7, options);
        var queriedSubgraph = await service.GetWorkflowRunGraphExportSubgraphAsync("run-1", 3, 9, options);

        agents.Should().ContainSingle().Which.Should().Be(new WorkflowAgentSummary("run-1", "WorkflowRunGAgent", "WorkflowRunGAgent[direct]"));
        currentState.Should().BeSameAs(snapshot);
        queriedTimeline.Should().Equal(timeline);
        queriedEdges.Should().Equal(edges);
        queriedSubgraph.Should().BeSameAs(subgraph);
        calls.Should().ContainInOrder(
            "ListWorkflowActorCurrentStates:200",
            "GetWorkflowActorCurrentState:run-1",
            "ListWorkflowRunTimelineExport:run-1:5",
            "GetWorkflowRunGraphExportEdges:run-1:7:Outbound:child",
            "GetWorkflowRunGraphExportSubgraph:run-1:3:9:Outbound:child");
    }

    [Fact]
    public async Task ScopeBoundQueries_ShouldRejectMismatchedOwnerBeforeReadingArtifacts()
    {
        var calls = new List<string>();
        var currentStatePort = new FakeCurrentStateQueryPort(calls)
        {
            WorkflowActorCurrentStateQueryEnabled = true,
            SingleSnapshot = new WorkflowActorSnapshot
            {
                ActorId = "run-victim",
                ScopeId = "victim-scope",
            },
        };
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog([]),
            currentStatePort,
            new FakeArtifactQueryPort(calls) { WorkflowArtifactQueryEnabled = true },
            new StaticWorkflowCatalogPort(),
            new StaticWorkflowCapabilitiesPort());
        IWorkflowExecutionScopeQueryApplicationService scopedService = service;

        var currentState = await scopedService.GetWorkflowActorCurrentStateAsync(
            "attacker-scope",
            "run-victim");
        var timeline = await scopedService.ListWorkflowRunTimelineExportAsync(
            "attacker-scope",
            "run-victim");
        var edges = await scopedService.ListWorkflowRunGraphExportEdgesAsync(
            "attacker-scope",
            "run-victim");
        var subgraph = await scopedService.GetWorkflowRunGraphExportSubgraphAsync(
            "attacker-scope",
            "run-victim");

        currentState.Should().BeNull();
        timeline.Should().BeNull();
        edges.Should().BeNull();
        subgraph.Should().BeNull();
        calls.Should().OnlyContain(call => call == "GetWorkflowActorCurrentState:run-victim");
    }

    [Fact]
    public async Task ListWorkflowActorCurrentStatesAsync_ShouldDelegateStructuredCurrentStateQuery()
    {
        var snapshot = new WorkflowActorSnapshot
        {
            ActorId = "run-dead-letter-1",
            WorkflowName = "orders",
            SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
            StateVersion = 33,
        };
        var calls = new List<string>();
        var currentStatePort = new FakeCurrentStateQueryPort(calls)
        {
            WorkflowActorCurrentStateQueryEnabled = true,
            Snapshots = [snapshot],
        };
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog([]),
            currentStatePort,
            new FakeArtifactQueryPort(calls),
            new StaticWorkflowCatalogPort(),
            new StaticWorkflowCapabilitiesPort());

        var result = await service.ListWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 17,
                SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
                ScopeId = "scope-a",
                DefinitionActorIds = ["def-a", "def-b"],
            });

        result.Should().ContainSingle().Which.Should().BeSameAs(snapshot);
        currentStatePort.LastListQuery.Should().NotBeNull();
        currentStatePort.LastListQuery!.Take.Should().Be(17);
        currentStatePort.LastListQuery.SagaStatus.Should().Be(WorkflowSagaStatus.CompensationDeadLetter);
        currentStatePort.LastListQuery.ScopeId.Should().Be("scope-a");
        currentStatePort.LastListQuery.DefinitionActorIds.Should().Equal("def-a", "def-b");
        calls.Should().Equal("ListWorkflowActorCurrentStatesQuery:17:CompensationDeadLetter:scope-a:def-a,def-b");
    }

    [Fact]
    public async Task ListAgentsAsync_ShouldHonorCancellation()
    {
        var service = new WorkflowExecutionQueryApplicationService(
            new StaticWorkflowDefinitionCatalog([]),
            new FakeCurrentStateQueryPort([]) { WorkflowActorCurrentStateQueryEnabled = false },
            new FakeArtifactQueryPort([]) { WorkflowArtifactQueryEnabled = false },
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
        public bool WorkflowActorCurrentStateQueryEnabled { get; set; }
        public IReadOnlyList<WorkflowActorSnapshot> Snapshots { get; init; } = [];
        public WorkflowActorSnapshot? SingleSnapshot { get; init; }
        public WorkflowActorCurrentStateListQuery? LastListQuery { get; private set; }

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            calls.Add($"GetWorkflowActorCurrentState:{actorId}");
            return Task.FromResult(SingleSnapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(int take = 200, CancellationToken ct = default)
        {
            calls.Add($"ListWorkflowActorCurrentStates:{take}");
            return Task.FromResult(Snapshots);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default)
        {
            LastListQuery = query;
            calls.Add(
                $"ListWorkflowActorCurrentStatesQuery:{query.Take}:{query.SagaStatus}:{query.ScopeId}:{string.Join(",", query.DefinitionActorIds)}");
            return Task.FromResult(Snapshots);
        }

        public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(string actorId, CancellationToken ct = default)
        {
            calls.Add($"GetWorkflowActorProjectionState:{actorId}");
            return Task.FromResult<WorkflowActorProjectionState?>(null);
        }
    }

    private sealed class FakeArtifactQueryPort(List<string> calls) : IWorkflowExecutionArtifactQueryPort
    {
        public bool WorkflowArtifactQueryEnabled { get; set; }
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
