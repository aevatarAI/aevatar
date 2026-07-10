using System.Text;
using System.Text.Json;
using Aevatar.Capabilities;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    public async Task ListAgents_WhenProjectionIndexDrifts_ShouldReturnServiceUnavailableDiagnostic()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            ListAgentsException = CreateProjectionIndexSchemaDriftException(),
        };

        var result = await ChatQueryEndpoints.ListAgents(service, CancellationToken.None);

        var http = await ExecuteWithContextAsync(result);
        var body = await ReadBodyAsync(http.Response);
        http.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("code").GetString()
            .Should().Be("workflow_execution_current_state_projection_index_drift");
        root.GetProperty("provider").GetString().Should().Be("Elasticsearch");
        root.GetProperty("indexAlias").GetString()
            .Should().Be("aevatar-mainnet-workflow-execution-current-states");
        root.GetProperty("currentPhysicalIndex").GetString()
            .Should().Be("aevatar-mainnet-workflow-execution-current-states-v6c660705");
        root.GetProperty("expectedPhysicalIndex").GetString()
            .Should().Be("aevatar-mainnet-workflow-execution-current-states-vddd647dc");
        root.GetProperty("remediation").GetString()
            .Should().Contain("Rebuild or repoint");
    }

    [Fact]
    public async Task ListAgentsRoute_WhenProjectionIndexDrifts_ShouldReturnServiceUnavailableDiagnostic()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            ListAgentsException = CreateProjectionIndexSchemaDriftException(),
        };
        await using var app = await CreateRouteAppAsync(service);
        using var client = CreateClient(app);

        var response = await client.GetAsync("/api/agents");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable, body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("code").GetString()
            .Should().Be("workflow_execution_current_state_projection_index_drift");
        root.GetProperty("indexAlias").GetString()
            .Should().Be("aevatar-mainnet-workflow-execution-current-states");
        root.GetProperty("currentPhysicalIndex").GetString()
            .Should().Be("aevatar-mainnet-workflow-execution-current-states-v6c660705");
        root.GetProperty("expectedPhysicalIndex").GetString()
            .Should().Be("aevatar-mainnet-workflow-execution-current-states-vddd647dc");
    }

    [Fact]
    public async Task WorkflowCapabilityHealthProbe_WhenProjectionIndexDrifts_ShouldReturnUnhealthyDiagnostic()
    {
        var probe = new FakeWorkflowExecutionCurrentStateIndexProbe
        {
            Result = new ProjectionIndexConsistencyResult(
                "Elasticsearch",
                "aevatar-mainnet-workflow-execution-current-states",
                "aevatar-mainnet-workflow-execution-current-states-vddd647dc",
                "aevatar-mainnet-workflow-execution-current-states-v6c660705",
                ProjectionIndexConsistencyStatus.Drifted,
                "Projection index schema drift detected: alias points to a physical index with a different schema fingerprint."),
        };
        var service = new FakeWorkflowExecutionQueryApplicationService();
        await using var app = CreateWorkflowCapabilityApp(service, probe);
        var healthService = CreateHealthService(app);

        var readiness = await healthService.GetReadinessAsync();

        readiness.Ok.Should().BeFalse();
        readiness.Status.Should().Be("not-ready");
        var component = readiness.Components.Should()
            .ContainSingle(x => x.Name == "workflow-bundle")
            .Subject;
        component.Status.Should().Be(Aevatar.Capabilities.AevatarHealthStatuses.Unhealthy);
        component.Message.Should().Contain("schema drift");
        component.Details["provider"].Should().Be("Elasticsearch");
        component.Details["indexAlias"].Should().Be("aevatar-mainnet-workflow-execution-current-states");
        component.Details["currentPhysicalIndex"].Should().Be("aevatar-mainnet-workflow-execution-current-states-v6c660705");
        component.Details["expectedPhysicalIndex"].Should().Be("aevatar-mainnet-workflow-execution-current-states-vddd647dc");
        component.Details["remediation"].Should().Contain("Rebuild or repoint");
        service.Calls.Should().BeEmpty();
        probe.CheckCalls.Should().Be(1);
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

        using var cts = new CancellationTokenSource();
        var result = await ChatQueryEndpoints.ListPrimitives(service, cts.Token);

        var body = await ExecuteAsync(result);
        body.Should().Contain("workflow_call");
        body.Should().Contain("sub_workflow");
        body.Should().Contain("child_example");
        body.Should().NotContain("ignored_non_example");
        service.Calls.Should().ContainInOrder("GetCapabilities", "ListWorkflowCatalog");
        service.CancellationTokens.Should().OnlyContain(token => token == cts.Token);
    }

    [Fact]
    public async Task CatalogEndpoints_ShouldAwaitAsyncQueryServiceAndPassCancellationToken()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            WorkflowCatalog =
            [
                new WorkflowCatalogItem
                {
                    Name = "direct",
                },
            ],
            WorkflowDetail = new WorkflowCatalogItemDetail
            {
                Catalog = new WorkflowCatalogItem
                {
                    Name = "direct",
                },
            },
            Capabilities = new WorkflowCapabilitiesDocument
            {
                SchemaVersion = "capabilities.v1",
            },
        };
        using var cts = new CancellationTokenSource();

        var catalog = await ChatQueryEndpoints.ListWorkflowCatalog(service, cts.Token);
        var capabilities = await ChatQueryEndpoints.GetCapabilities(service, cts.Token);
        var detail = await ChatQueryEndpoints.GetWorkflowDetail("direct", service, cts.Token);

        (await ExecuteAsync(catalog)).Should().Contain("direct");
        (await ExecuteAsync(capabilities)).Should().Contain("capabilities.v1");
        (await ExecuteAsync(detail)).Should().Contain("direct");
        service.Calls.Should().ContainInOrder(
            "ListWorkflowCatalog",
            "GetCapabilities",
            "GetWorkflowDetail:direct");
        service.CancellationTokens.Should().OnlyContain(token => token == cts.Token);
    }

    [Fact]
    public async Task GetWorkflowActorCurrentState_ShouldReturnNotFound_WhenCurrentStateMissing()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService();

        var result = await ChatQueryEndpoints.GetWorkflowActorCurrentState("actor-1", service, CancellationToken.None);

        var http = await ExecuteWithContextAsync(result);
        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        service.Calls.Should().ContainSingle().Which.Should().Be("GetWorkflowActorCurrentState:actor-1");
    }

    [Fact]
    public async Task GetWorkflowActorCurrentStateRoute_ShouldSerializeCompensationDeadLetter()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            Snapshot = new WorkflowActorSnapshot
            {
                ActorId = "run-dead-letter",
                WorkflowName = "refund_order",
                CompletionStatus = WorkflowRunCompletionStatus.Failed,
                StateVersion = 42,
                SagaStatus = WorkflowSagaStatus.CompensationDeadLetter,
                DeadLetterFailedCompensationStepId = "refund_payment",
                DeadLetterRemainingUncompensated = 2,
                DeadLetterError = "refund provider rejected compensation",
            },
        };
        await using var app = await CreateRouteAppAsync(service);
        using var client = CreateClient(app);

        var response = await client.GetAsync("/api/workflow-actors/run-dead-letter/current-state");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("sagaStatus").GetString().Should().Be("CompensationDeadLetter");
        var deadLetter = root.GetProperty("deadLetter");
        deadLetter.GetProperty("failedCompensationStepId").GetString().Should().Be("refund_payment");
        deadLetter.GetProperty("remainingUncompensated").GetInt32().Should().Be(2);
        deadLetter.GetProperty("error").GetString().Should().Be("refund provider rejected compensation");
        service.Calls.Should().ContainSingle()
            .Which.Should().Be("GetWorkflowActorCurrentState:run-dead-letter");
    }

    [Fact]
    public async Task GraphEndpoints_ShouldNormalizeDirectionAndEdgeTypes()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            GraphEdges =
            [
                new WorkflowRunGraphExportEdge
                {
                    EdgeId = "edge-1",
                    FromNodeId = "actor-1",
                    ToNodeId = "actor-2",
                },
            ],
            GraphSubgraph = new WorkflowRunGraphExportSubgraph
            {
                RootNodeId = "actor-1",
            },
        };

        var edgesResult = await ChatQueryEndpoints.ListWorkflowRunGraphExportEdges(
            "actor-1",
            service,
            take: 12,
            direction: " outbound ",
            edgeTypes: ["child", " child ", "", "sibling"],
            ct: CancellationToken.None);
        var subgraphResult = await ChatQueryEndpoints.GetWorkflowRunGraphExportSubgraph(
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
            "ListWorkflowRunGraphExportEdges:actor-1:12:Outbound:child,sibling",
            "GetWorkflowRunGraphExportSubgraph:actor-1:3:8:Both:child");
    }

    [Fact]
    public async Task GetWorkflowRunGraphExportEnriched_ShouldReturnSubgraphOnly()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            GraphSubgraph = new WorkflowRunGraphExportSubgraph
            {
                RootNodeId = "run-1",
            },
        };

        var result = await ChatQueryEndpoints.GetWorkflowRunGraphExportEnriched(
            "run-1",
            service,
            depth: 4,
            take: 9,
            direction: " inbound ",
            edgeTypes: ["child", "child", ""],
            ct: CancellationToken.None);

        var body = await ExecuteAsync(result);
        body.Should().Contain("run-1");
        service.Calls.Should().ContainSingle()
            .Which.Should().Be("GetWorkflowRunGraphExportSubgraph:run-1:4:9:Inbound:child");
    }

    [Fact]
    public async Task Timeline_ShouldReturnResults()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            Timeline =
            [
                new WorkflowRunTimelineExportItem
                {
                    Stage = "completed",
                    StepId = "step-1",
                },
            ],
        };

        var timelineResult = await ChatQueryEndpoints.ListWorkflowRunTimelineExport("actor-1", service, 15, CancellationToken.None);

        (await ExecuteAsync(timelineResult)).Should().Contain("step-1");
        service.Calls.Should().Contain("ListWorkflowRunTimelineExport:actor-1:15");
    }

    [Fact]
    public async Task WorkflowRunExportRoutes_ShouldBindActorIdAndQueryParameters()
    {
        var service = new FakeWorkflowExecutionQueryApplicationService
        {
            Snapshot = new WorkflowActorSnapshot
            {
                ActorId = "run-42",
                WorkflowName = "direct",
            },
            Timeline =
            [
                new WorkflowRunTimelineExportItem
                {
                    Stage = "completed",
                    StepId = "step-1",
                },
            ],
            GraphEdges =
            [
                new WorkflowRunGraphExportEdge
                {
                    EdgeId = "edge-1",
                    FromNodeId = "run-42",
                    ToNodeId = "child-1",
                    EdgeType = "child",
                },
            ],
            GraphSubgraph = new WorkflowRunGraphExportSubgraph
            {
                RootNodeId = "run-42",
                Nodes =
                {
                    new WorkflowRunGraphExportNode
                    {
                        NodeId = "run-42",
                        NodeType = "workflow_run",
                    },
                },
                Edges =
                {
                    new WorkflowRunGraphExportEdge
                    {
                        EdgeId = "edge-1",
                        FromNodeId = "run-42",
                        ToNodeId = "child-1",
                        EdgeType = "child",
                    },
                },
            },
        };

        await using var app = await CreateRouteAppAsync(service);
        using var client = CreateClient(app);

        var timeline = await client.GetAsync("/api/workflow-runs/run-42/timeline-export?take=7");
        var edges = await client.GetAsync("/api/workflow-runs/run-42/graph-export/edges?take=8&direction=outbound&edgeTypes=child&edgeTypes=sibling");
        var subgraph = await client.GetAsync("/api/workflow-runs/run-42/graph-export/subgraph?depth=3&take=9&direction=inbound&edgeTypes=child");
        var enriched = await client.GetAsync("/api/workflow-runs/run-42/graph-export/enriched?depth=4&take=10&direction=both&edgeTypes=child");

        timeline.EnsureSuccessStatusCode();
        edges.EnsureSuccessStatusCode();
        subgraph.EnsureSuccessStatusCode();
        enriched.EnsureSuccessStatusCode();
        (await timeline.Content.ReadAsStringAsync()).Should().Contain("step-1");
        (await edges.Content.ReadAsStringAsync()).Should().Contain("edge-1");
        (await subgraph.Content.ReadAsStringAsync()).Should().Contain("run-42");
        (await enriched.Content.ReadAsStringAsync()).Should().Contain("run-42");
        service.Calls.Should().ContainInOrder(
            "ListWorkflowRunTimelineExport:run-42:7",
            "ListWorkflowRunGraphExportEdges:run-42:8:Outbound:child,sibling",
            "GetWorkflowRunGraphExportSubgraph:run-42:3:9:Inbound:child",
            "GetWorkflowRunGraphExportSubgraph:run-42:4:10:Both:child");
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
                .Configure<JsonOptions>(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
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

    private static async Task<WebApplication> CreateRouteAppAsync(IWorkflowExecutionQueryApplicationService service)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(service);
        var app = builder.Build();
        ChatQueryEndpoints.Map(app.MapGroup("/api"));
        await app.StartAsync();
        return app;
    }

    private static WebApplication CreateWorkflowCapabilityApp(
        IWorkflowExecutionQueryApplicationService service,
        IProjectionIndexConsistencyProbe<WorkflowExecutionCurrentStateDocument>? indexProbe = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddOptions();
        builder.Services.AddSingleton(new Aevatar.Capabilities.AevatarHostMetadata
        {
            ServiceName = "workflow-test",
        });
        builder.Services.AddSingleton<Aevatar.Capabilities.AevatarHostHealthService>();
        builder.AddWorkflowCapabilityBundle();
        builder.Services.Replace(ServiceDescriptor.Singleton(service));
        if (indexProbe != null)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton(indexProbe));
        }

        var app = builder.Build();
        app.MapWorkflowCapabilityEndpoints();
        return app;
    }

    private static AevatarHostHealthService CreateHealthService(WebApplication app)
    {
        var routeBuilder = (IEndpointRouteBuilder)app;
        return new AevatarHostHealthService(
            app.Services,
            app.Services.GetServices<AevatarHealthContributorRegistration>(),
            routeBuilder.DataSources,
            app.Services.GetRequiredService<AevatarHostMetadata>());
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        return new HttpClient
        {
            BaseAddress = new Uri(address),
        };
    }

    private static ProjectionIndexSchemaDriftException CreateProjectionIndexSchemaDriftException() =>
        new(
            "Elasticsearch",
            "aevatar-mainnet-workflow-execution-current-states",
            "aevatar-mainnet-workflow-execution-current-states-v6c660705",
            "aevatar-mainnet-workflow-execution-current-states-vddd647dc");

    private sealed class FakeWorkflowExecutionQueryApplicationService : IWorkflowExecutionQueryApplicationService
    {
        public bool WorkflowActorCurrentStateQueryEnabled => true;
        public IReadOnlyList<WorkflowAgentSummary> Agents { get; init; } = [];
        public IReadOnlyList<string> Workflows { get; init; } = [];
        public IReadOnlyList<WorkflowCatalogItem> WorkflowCatalog { get; init; } = [];
        public WorkflowCatalogItemDetail? WorkflowDetail { get; init; }
        public WorkflowCapabilitiesDocument Capabilities { get; init; } = new();
        public WorkflowActorSnapshot? Snapshot { get; init; }
        public WorkflowRunReport? Report { get; init; }
        public IReadOnlyList<WorkflowRunTimelineExportItem> Timeline { get; init; } = [];
        public IReadOnlyList<WorkflowRunGraphExportEdge> GraphEdges { get; init; } = [];
        public WorkflowRunGraphExportSubgraph GraphSubgraph { get; init; } = new();
        public Exception? ListAgentsException { get; init; }
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default)
        {
            Calls.Add("ListAgents");
            if (ListAgentsException != null)
                throw ListAgentsException;

            return Task.FromResult(Agents);
        }

        public IReadOnlyList<string> ListWorkflows()
        {
            Calls.Add("ListWorkflows");
            return Workflows;
        }

        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default)
        {
            Calls.Add("ListWorkflowCatalog");
            CancellationTokens.Add(ct);
            return Task.FromResult(WorkflowCatalog);
        }

        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(
            string workflowName,
            CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowDetail:{workflowName}");
            CancellationTokens.Add(ct);
            return Task.FromResult(WorkflowDetail);
        }

        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default)
        {
            Calls.Add("GetCapabilities");
            CancellationTokens.Add(ct);
            return Task.FromResult(Capabilities);
        }

        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowActorCurrentState:{actorId}");
            return Task.FromResult(Snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
            WorkflowActorCurrentStateListQuery query,
            CancellationToken ct = default)
        {
            Calls.Add($"ListWorkflowActorCurrentStatesQuery:{query.Take}:{query.SagaStatus}:{query.ScopeId}:{string.Join(",", query.DefinitionActorIds)}");
            return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);
        }

        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string actorId, CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowRunReportArtifact:{actorId}");
            return Task.FromResult(Report);
        }

        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(string actorId, int take = 200, CancellationToken ct = default)
        {
            Calls.Add($"ListWorkflowRunTimelineExport:{actorId}:{take}");
            return Task.FromResult(Timeline);
        }

        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(string actorId, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default)
        {
            Calls.Add($"ListWorkflowRunGraphExportEdges:{actorId}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(GraphEdges);
        }

        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(string actorId, int depth = 2, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default)
        {
            Calls.Add($"GetWorkflowRunGraphExportSubgraph:{actorId}:{depth}:{take}:{options?.Direction}:{string.Join(",", options?.EdgeTypes ?? [])}");
            return Task.FromResult(GraphSubgraph);
        }
    }

    private sealed class FakeWorkflowExecutionCurrentStateIndexProbe
        : IProjectionIndexConsistencyProbe<WorkflowExecutionCurrentStateDocument>
    {
        public ProjectionIndexConsistencyResult Result { get; init; } = new(
            "Elasticsearch",
            "workflow-execution-current-states",
            "workflow-execution-current-states-vexpected",
            "workflow-execution-current-states-vexpected",
            ProjectionIndexConsistencyStatus.Consistent,
            "Projection index alias points to the expected physical index.");

        public int CheckCalls { get; private set; }

        public Task<ProjectionIndexConsistencyResult> CheckIndexConsistencyAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CheckCalls++;
            return Task.FromResult(Result);
        }
    }
}
