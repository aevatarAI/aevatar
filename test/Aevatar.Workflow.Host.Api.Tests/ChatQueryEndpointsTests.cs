using System.Text;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class ChatQueryEndpointsTests
{
    [Fact]
    public async Task ListAgents_ShouldReturnAgentsFromQueryService()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            Agents = [new WorkflowAgentSummary("actor-1", "WorkflowRunGAgent", "WorkflowRunGAgent[direct]")],
        };

        var result = await ChatQueryEndpoints.ListAgents(service, CancellationToken.None);

        var body = await ExecuteAsync(result);
        body.Should().Contain("actor-1");
        service.Calls.Should().ContainSingle().Which.Should().Be("ListAgents");
    }

    [Fact]
    public async Task ListWorkflows_ShouldReturnWorkflowNames()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            Workflows = ["direct", "auto"],
        };

        var result = ChatQueryEndpoints.ListWorkflows(service);

        var body = await ExecuteAsync(result);
        body.Should().Contain("direct");
        body.Should().Contain("auto");
    }

    [Fact]
    public async Task ListPrimitives_ShouldComposePrimitiveDescriptorsFromCapabilitiesAndCatalog()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            Capabilities = new WorkflowCapabilitiesDocument
            {
                Primitives =
                [
                    new WorkflowPrimitiveCapability
                    {
                        Name = "workflow_call",
                        Aliases = ["workflow_call", "sub_workflow"],
                        Category = "control-flow",
                        Description = "Invoke a sub workflow.",
                        Parameters =
                        [
                            new WorkflowPrimitiveParameterCapability
                            {
                                Name = "workflow",
                                Type = "string",
                                Required = true,
                                Description = "Workflow name.",
                                Default = string.Empty,
                                Enum = ["child", "parent", "child"],
                            },
                        ],
                    },
                ],
            },
            WorkflowCatalog =
            [
                new WorkflowCatalogItem
                {
                    Name = "child_example",
                    IsPrimitiveExample = true,
                    Primitives = ["workflow_call"],
                },
                new WorkflowCatalogItem
                {
                    Name = "ignored_non_example",
                    IsPrimitiveExample = false,
                    Primitives = ["workflow_call"],
                },
            ],
        };

        var result = ChatQueryEndpoints.ListPrimitives(service);

        var body = await ExecuteAsync(result);
        body.Should().Contain("workflow_call");
        body.Should().Contain("sub_workflow");
        body.Should().Contain("child_example");
        body.Should().NotContain("ignored_non_example");
        service.Calls.Should().ContainInOrder("GetCapabilities", "ListWorkflowCatalog");
    }

    [Fact]
    public async Task GetActorSnapshot_ShouldReturnNotFound_WhenSnapshotMissing()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService();

        var result = await ChatQueryEndpoints.GetActorSnapshot("actor-1", service, CancellationToken.None);

        var http = await ExecuteWithContextAsync(result);
        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        service.Calls.Should().ContainSingle().Which.Should().Be("GetActorSnapshot:actor-1");
    }

    [Fact]
    public async Task GraphEndpoints_ShouldNormalizeDirectionAndEdgeTypes()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            GraphEdges =
            [
                new WorkflowActorGraphEdge
                {
                    EdgeId = "edge-1",
                    FromNodeId = "actor-1",
                    ToNodeId = "actor-2",
                },
            ],
            GraphSubgraph = new WorkflowActorGraphSubgraph
            {
                RootNodeId = "actor-1",
            },
        };

        var edgesResult = await ChatQueryEndpoints.ListActorGraphEdges(
            "actor-1",
            service,
            take: 12,
            direction: " outbound ",
            edgeTypes: ["child", " child ", "", "sibling"],
            ct: CancellationToken.None);
        var subgraphResult = await ChatQueryEndpoints.GetActorGraphSubgraph(
            "actor-1",
            service,
            depth: 3,
            take: 8,
            direction: "invalid",
            edgeTypes: ["child", "child", "  "],
            ct: CancellationToken.None);

        (await ExecuteAsync(edgesResult)).Should().Contain("edge-1");
        (await ExecuteAsync(subgraphResult)).Should().Contain("actor-1");
        service.Calls.Should().ContainInOrder(
            "ListActorGraphEdges:actor-1:12:Outbound:child,sibling",
            "GetActorGraphSubgraph:actor-1:3:8:Both:child");
    }

    [Fact]
    public async Task GetActorGraphEnriched_ShouldCombineSnapshotAndSubgraph()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            Snapshot = new WorkflowActorSnapshot
            {
                ActorId = "actor-1",
                WorkflowName = "direct",
            },
            GraphSubgraph = new WorkflowActorGraphSubgraph
            {
                RootNodeId = "actor-1",
            },
        };

        var result = await ChatQueryEndpoints.GetActorGraphEnriched(
            "actor-1",
            service,
            depth: 4,
            take: 9,
            direction: " inbound ",
            edgeTypes: ["child", "child", ""],
            ct: CancellationToken.None);

        var body = await ExecuteAsync(result);
        body.Should().Contain("snapshot");
        body.Should().Contain("subgraph");
        body.Should().Contain("actor-1");
        service.Calls.Should().ContainInOrder(
            "GetActorSnapshot:actor-1",
            "GetActorGraphSubgraph:actor-1:4:9:Inbound:child");
    }

    [Fact]
    public async Task Timeline_ShouldReturnResults()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            Timeline =
            [
                new WorkflowActorTimelineItem
                {
                    Stage = "completed",
                    StepId = "step-1",
                },
            ],
        };

        var timelineResult = await ChatQueryEndpoints.ListActorTimeline("actor-1", service, 15, CancellationToken.None);

        (await ExecuteAsync(timelineResult)).Should().Contain("step-1");
        service.Calls.Should().Contain("ListActorTimeline:actor-1:15");
    }

    private static async Task<string> ExecuteAsync(IResult result)
    {
        var http = await ExecuteWithContextAsync(result);
        return await ReadBodyAsync(http.Response);
    }

    private static async Task<DefaultHttpContext> ExecuteWithContextAsync(IResult result)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddOptions()
                .BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();
        await result.ExecuteAsync(http);
        return http;
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeWorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
    {
        public bool ActorQueryEnabled => true;
        public IReadOnlyList<WorkflowAgentSummary> Agents { get; init; } = [];
        public IReadOnlyList<string> Workflows { get; init; } = [];
        public IReadOnlyList<WorkflowCatalogItem> WorkflowCatalog { get; init; } = [];
        public WorkflowCatalogItemDetail? WorkflowDetail { get; init; }
        public WorkflowCapabilitiesDocument Capabilities { get; init; } = new();
        public WorkflowActorSnapshot? Snapshot { get; init; }
        public WorkflowRunReport? Report { get; init; }
        public IReadOnlyList<WorkflowActorTimelineItem> Timeline { get; init; } = [];
        public IReadOnlyList<WorkflowActorGraphEdge> GraphEdges { get; init; } = [];
        public WorkflowActorGraphSubgraph GraphSubgraph { get; init; } = new();
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default)
        {
            Calls.Add("ListAgents");
            return Task.FromResult(Agents);
        }

        public IReadOnlyList<string> ListWorkflows()
        {
            Calls.Add("ListWorkflows");
            return Workflows;
        }

        public IReadOnlyList<WorkflowCatalogItem> ListWorkflowCatalog()
        {
            Calls.Add("ListWorkflowCatalog");
            return WorkflowCatalog;
        }

        public WorkflowCatalogItemDetail? GetWorkflowDetail(string workflowName)
        {
            Calls.Add($"GetWorkflowDetail:{workflowName}");
            return WorkflowDetail;
        }

        public WorkflowCapabilitiesDocument GetCapabilities()
        {
            Calls.Add("GetCapabilities");
            return Capabilities;
        }

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            Calls.Add($"GetActorSnapshot:{actorId}");
            return Task.FromResult(Snapshot);
        }

        public Task<WorkflowRunReport?> GetActorReportAsync(string actorId, CancellationToken ct = default)
        {
            Calls.Add($"GetActorReport:{actorId}");
            return Task.FromResult(Report);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default)
        {
            Calls.Add($"ListActorTimeline:{actorId}:{take}");
            return Task.FromResult(Timeline);
        }

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> ListActorGraphEdgesAsync(string actorId, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default)
        {
            Calls.Add($"ListActorGraphEdges:{actorId}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(GraphEdges);
        }

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(string actorId, int depth = 2, int take = 200, WorkflowActorGraphQueryOptions? options = null, CancellationToken ct = default)
        {
            Calls.Add($"GetActorGraphSubgraph:{actorId}:{depth}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(GraphSubgraph);
        }
    }
}
