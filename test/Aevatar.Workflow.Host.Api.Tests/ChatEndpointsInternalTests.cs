using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Host.Api.Tests;

public class ChatEndpointsInternalTests
{
    [Fact]
    public void MapWorkflowCapabilityEndpoints_ShouldRegisterCoreRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>>(new FakeChatRunApplicationService());
        var queryService = new FakeQueryService { ActorQueryEnabledValue = true };
        builder.Services.AddSingleton<IWorkflowExecutionQueryApplicationService>(queryService);

        var app = builder.Build();
        var endpoints = (IEndpointRouteBuilder)app;

        var returned = app.MapWorkflowCapabilityEndpoints();
        var routePatterns = endpoints.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        returned.Should().BeSameAs(app);
        routePatterns.Should().Contain("/api/chat");
        routePatterns.Should().Contain("/api/agents");
        routePatterns.Should().Contain("/api/workflows");
        routePatterns.Should().Contain("/api/ws/chat");
        routePatterns.Should().Contain("/api/actors/{actorId}");
        routePatterns.Should().Contain("/api/actors/{actorId}/timeline");
        routePatterns.Should().Contain("/api/actors/{actorId}/graph-edges");
        routePatterns.Should().Contain("/api/actors/{actorId}/graph-subgraph");
    }

    [Fact]
    public async Task HandleChat_WhenUseCaseReturnsWorkflowMissing_ShouldReturn404()
    {
        var http = CreateHttpContext();
        var service = new FakeChatRunApplicationService
        {
            ExecuteHandler = (_, _, _, _) => Task.FromResult(ToCoreResult(
                new WorkflowChatRunExecutionResult(
                    WorkflowChatRunStartError.WorkflowNotFound,
                    null,
                    null))),
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello", Workflow = "missing" },
            service,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleChat_WhenSucceeded_ShouldWriteSseFrame()
    {
        var http = CreateHttpContext();
        var service = new FakeChatRunApplicationService
        {
            ExecuteHandler = async (_, emitAsync, onStartedAsync, ct) =>
            {
                var started = new WorkflowChatRunStarted("actor-1", "direct", "cmd-1");
                if (onStartedAsync != null)
                    await onStartedAsync(started, ct);

                await emitAsync(new WorkflowOutputFrame
                {
                    Type = WorkflowRunEventTypes.RunStarted,
                    ThreadId = "actor-1",
                }, ct);

                await emitAsync(new WorkflowOutputFrame
                {
                    Type = WorkflowRunEventTypes.RunFinished,
                    ThreadId = "actor-1",
                }, ct);

                return ToCoreResult(
                        new WorkflowChatRunExecutionResult(
                            WorkflowChatRunStartError.None,
                            started,
                            new WorkflowChatRunFinalizeResult(
                                WorkflowProjectionCompletionStatus.Completed,
                                true)));
            },
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var body = await ReadBodyAsync(http);
        body.Should().Contain("data:");
        body.Should().Contain(WorkflowRunEventTypes.RunStarted);
        body.Should().Contain(WorkflowRunEventTypes.RunFinished);
    }

    [Fact]
    public async Task HandleChat_WhenWorkflowYamlProvided_ShouldForwardWorkflowYamlToRequest()
    {
        var http = CreateHttpContext();
        WorkflowChatRunRequest? captured = null;
        var service = new FakeChatRunApplicationService
        {
            ExecuteHandler = (request, _, _, _) =>
            {
                captured = request;
                return Task.FromResult(ToCoreResult(
                    new WorkflowChatRunExecutionResult(
                        WorkflowChatRunStartError.WorkflowNotFound,
                        null,
                        null)));
            },
        };

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput
            {
                Prompt = "hello",
                WorkflowYaml = """
                               name: inline_direct
                               roles:
                                 - id: assistant
                                   name: Assistant
                               steps:
                                 - id: reply
                                   type: llm_call
                                   role: assistant
                               """,
            },
            service,
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.WorkflowYaml.Should().Contain("name: inline_direct");
    }

    [Fact]
    public async Task HandleCommand_WhenStarted_ShouldReturnAcceptedCommandId()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var service = new FakeChatRunApplicationService
        {
            ExecuteHandler = async (_, _, onStartedAsync, ct) =>
            {
                var started = new WorkflowChatRunStarted("actor-1", "direct", "cmd-1");
                if (onStartedAsync != null)
                    await onStartedAsync(started, ct);

                return ToCoreResult(
                    new WorkflowChatRunExecutionResult(
                        WorkflowChatRunStartError.None,
                        started,
                        new WorkflowChatRunFinalizeResult(
                            WorkflowProjectionCompletionStatus.Completed,
                            true)));
            },
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status202Accepted);
        doc.RootElement.GetProperty("commandId").GetString().Should().Be("cmd-1");
    }

    [Fact]
    public async Task HandleChat_WithEmptyPrompt_ShouldReturn400()
    {
        var http = CreateHttpContext();
        var service = new FakeChatRunApplicationService();

        await WorkflowCapabilityEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "  " },
            service,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleCommand_WithEmptyPrompt_ShouldReturn400WithCode()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var service = new FakeChatRunApplicationService();

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "" },
            service,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        doc.RootElement.GetProperty("code").GetString().Should().Be("INVALID_PROMPT");
    }

    [Fact]
    public async Task HandleCommand_WhenExecutionThrows_ShouldReturn500WithStructuredBody()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var service = new FakeChatRunApplicationService
        {
            ExecuteHandler = (_, _, _, _) => throw new InvalidOperationException("projection init failed"),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status500InternalServerError);
        doc.RootElement.GetProperty("code").GetString().Should().Be("EXECUTION_FAILED");
    }

    [Fact]
    public async Task HandleCommand_WhenWorkflowYamlInvalid_ShouldReturn400WithStructuredCode()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var service = new FakeChatRunApplicationService
        {
            ExecuteHandler = (_, _, _, _) => Task.FromResult(ToCoreResult(
                new WorkflowChatRunExecutionResult(
                    WorkflowChatRunStartError.InvalidWorkflowYaml,
                    null,
                    null))),
        };

        var result = await WorkflowCapabilityEndpoints.HandleCommand(
            new ChatInput
            {
                Prompt = "hello",
                WorkflowYaml = "invalid",
            },
            service,
            loggerFactory,
            CancellationToken.None);

        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status400BadRequest);
        doc.RootElement.GetProperty("code").GetString().Should().Be("INVALID_WORKFLOW_YAML");
    }

    [Fact]
    public async Task GetActorSnapshot_ShouldReturnSnapshot()
    {
        var queryService = new FakeQueryService
        {
            ActorQueryEnabledValue = true,
            SnapshotByActorId = new Dictionary<string, WorkflowActorSnapshot>(StringComparer.Ordinal)
            {
                ["actor-1"] = new WorkflowActorSnapshot
                {
                    ActorId = "actor-1",
                    WorkflowName = "direct",
                    LastCommandId = "cmd-1",
                    TotalSteps = 3,
                },
            },
        };

        var result = await ChatQueryEndpoints.GetActorSnapshot("actor-1", queryService, CancellationToken.None);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.GetProperty("actorId").GetString().Should().Be("actor-1");
        doc.RootElement.GetProperty("totalSteps").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task GetActorSnapshot_WhenMissing_ShouldReturnNotFound()
    {
        var queryService = new FakeQueryService { ActorQueryEnabledValue = true };

        var result = await ChatQueryEndpoints.GetActorSnapshot("missing", queryService, CancellationToken.None);
        var (statusCode, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ListActorTimeline_ShouldReturnTimelineItems()
    {
        var queryService = new FakeQueryService
        {
            ActorQueryEnabledValue = true,
            TimelineByActorId = new Dictionary<string, IReadOnlyList<WorkflowActorTimelineItem>>(StringComparer.Ordinal)
            {
                ["actor-1"] =
                [
                    new WorkflowActorTimelineItem
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        Stage = "workflow.start",
                        Message = "started",
                    },
                ],
            },
        };

        var result = await ChatQueryEndpoints.ListActorTimeline("actor-1", queryService, 50, CancellationToken.None);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("stage").GetString().Should().Be("workflow.start");
    }

    [Fact]
    public async Task ListActorGraphEdges_ShouldReturnRelationItems()
    {
        var queryService = new FakeQueryService
        {
            ActorQueryEnabledValue = true,
            RelationsByActorId = new Dictionary<string, IReadOnlyList<WorkflowActorGraphEdge>>(StringComparer.Ordinal)
            {
                ["actor-1"] =
                [
                    new WorkflowActorGraphEdge
                    {
                        EdgeId = "edge-1",
                        FromNodeId = "actor-1",
                        ToNodeId = "actor-2",
                        EdgeType = "CHILD_OF",
                    },
                ],
            },
        };

        var result = await ChatQueryEndpoints.ListActorGraphEdges("actor-1", queryService, 50, ct: CancellationToken.None);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("edgeId").GetString().Should().Be("edge-1");
    }

    [Fact]
    public async Task ListActorGraphEdges_WhenDirectionAndEdgeTypesProvided_ShouldForwardQueryOptions()
    {
        var queryService = new FakeQueryService
        {
            ActorQueryEnabledValue = true,
        };

        var result = await ChatQueryEndpoints.ListActorGraphEdges(
            "actor-1",
            queryService,
            50,
            direction: "Outbound",
            edgeTypes: ["CHILD_OF", "OWNS"],
            ct: CancellationToken.None);
        var (statusCode, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status200OK);
        queryService.LastGraphQueryOptions.Should().NotBeNull();
        queryService.LastGraphQueryOptions!.Direction.Should().Be(WorkflowActorGraphDirection.Outbound);
        queryService.LastGraphQueryOptions.EdgeTypes.Should().BeEquivalentTo(["CHILD_OF", "OWNS"]);
    }

    [Fact]
    public async Task GetActorGraphSubgraph_ShouldReturnSubgraph()
    {
        var queryService = new FakeQueryService
        {
            ActorQueryEnabledValue = true,
            SubgraphByActorId = new Dictionary<string, WorkflowActorGraphSubgraph>(StringComparer.Ordinal)
            {
                ["actor-1"] = new WorkflowActorGraphSubgraph
                {
                    RootNodeId = "actor-1",
                    Nodes =
                    [
                        new WorkflowActorGraphNode
                        {
                            NodeId = "actor-1",
                            NodeType = "Actor",
                        },
                        new WorkflowActorGraphNode
                        {
                            NodeId = "actor-2",
                            NodeType = "Actor",
                        },
                    ],
                    Edges =
                    [
                        new WorkflowActorGraphEdge
                        {
                            EdgeId = "edge-1",
                            FromNodeId = "actor-1",
                            ToNodeId = "actor-2",
                            EdgeType = "CHILD_OF",
                        },
                    ],
                },
            },
        };

        var result = await ChatQueryEndpoints.GetActorGraphSubgraph("actor-1", queryService, 2, 50, ct: CancellationToken.None);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.GetProperty("rootNodeId").GetString().Should().Be("actor-1");
        doc.RootElement.GetProperty("nodes").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("edges").GetArrayLength().Should().Be(1);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        return new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream(),
            },
        };
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext http)
    {
        http.Response.Body.Position = 0;
        return await new StreamReader(http.Response.Body).ReadToEndAsync();
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result)
    {
        var http = new DefaultHttpContext
        {
            Response =
            {
                Body = new MemoryStream(),
            },
        };
        http.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http.Response.StatusCode, body);
    }

    private sealed class FakeChatRunApplicationService : ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>
    {
        public Func<WorkflowChatRunRequest, Func<WorkflowOutputFrame, CancellationToken, ValueTask>, Func<WorkflowChatRunStarted, CancellationToken, ValueTask>?, CancellationToken, Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>>>
            ExecuteHandler { get; set; } = (_, _, _, _) => Task.FromResult(ToCoreResult(
                new WorkflowChatRunExecutionResult(
                    WorkflowChatRunStartError.None,
                    null,
                    null)));

        public Task<CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>> ExecuteAsync(
            WorkflowChatRunRequest command,
            Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
            Func<WorkflowChatRunStarted, CancellationToken, ValueTask>? onStartedAsync = null,
            CancellationToken ct = default)
        {
            return ExecuteHandler(command, emitAsync, onStartedAsync, ct);
        }
    }

    private sealed class FakeQueryService :
        IWorkflowExecutionQueryApplicationService
    {
        public bool ActorQueryEnabledValue { get; set; }
        public IReadOnlyList<WorkflowAgentSummary> Agents { get; set; } = [];
        public IReadOnlyList<string> Workflows { get; set; } = [];
        public Dictionary<string, WorkflowActorSnapshot> SnapshotByActorId { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<WorkflowActorTimelineItem>> TimelineByActorId { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<WorkflowActorGraphEdge>> RelationsByActorId { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, WorkflowActorGraphSubgraph> SubgraphByActorId { get; set; } = new(StringComparer.Ordinal);
        public WorkflowActorGraphQueryOptions? LastGraphQueryOptions { get; private set; }

        public bool ActorQueryEnabled => ActorQueryEnabledValue;

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult(Agents);

        public IReadOnlyList<string> ListWorkflows() => Workflows;

        public string? GetWorkflowYaml(string name) => null;

        public Task<WorkflowActorSnapshot?> GetActorSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            SnapshotByActorId.TryGetValue(actorId, out var snapshot);
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<WorkflowActorTimelineItem>> ListActorTimelineAsync(string actorId, int take = 200, CancellationToken ct = default)
        {
            if (!TimelineByActorId.TryGetValue(actorId, out var items))
                items = [];

            return Task.FromResult<IReadOnlyList<WorkflowActorTimelineItem>>(items.Take(Math.Max(1, take)).ToList());
        }

        public Task<IReadOnlyList<WorkflowActorGraphEdge>> ListActorGraphEdgesAsync(
            string actorId,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            LastGraphQueryOptions = options;
            _ = options;
            if (!RelationsByActorId.TryGetValue(actorId, out var items))
                items = [];

            return Task.FromResult<IReadOnlyList<WorkflowActorGraphEdge>>(items.Take(Math.Max(1, take)).ToList());
        }

        public Task<WorkflowActorGraphSubgraph> GetActorGraphSubgraphAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            LastGraphQueryOptions = options;
            _ = depth;
            _ = take;
            _ = options;
            if (!SubgraphByActorId.TryGetValue(actorId, out var item))
            {
                item = new WorkflowActorGraphSubgraph
                {
                    RootNodeId = actorId,
                };
            }

            return Task.FromResult(item);
        }

        public async Task<WorkflowActorGraphEnrichedSnapshot?> GetActorGraphEnrichedSnapshotAsync(
            string actorId,
            int depth = 2,
            int take = 200,
            WorkflowActorGraphQueryOptions? options = null,
            CancellationToken ct = default)
        {
            var snapshot = await GetActorSnapshotAsync(actorId, ct);
            if (snapshot == null)
                return null;

            var subgraph = await GetActorGraphSubgraphAsync(actorId, depth, take, options, ct);
            return new WorkflowActorGraphEnrichedSnapshot
            {
                Snapshot = snapshot,
                Subgraph = subgraph,
            };
        }
    }

    private static CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> ToCoreResult(
        WorkflowChatRunExecutionResult source) =>
        new(source.Error, source.Started, source.FinalizeResult);
}
