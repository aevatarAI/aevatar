using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Host.Api.Endpoints;
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
    public void MapChatEndpoints_ShouldRegisterCoreRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>>(new FakeChatRunApplicationService());
        var queryService = new FakeQueryService { ActorQueryEnabledValue = true };
        builder.Services.AddSingleton<IWorkflowExecutionQueryApplicationService>(queryService);

        var app = builder.Build();
        var endpoints = (IEndpointRouteBuilder)app;

        var returned = app.MapChatEndpoints();
        var routePatterns = endpoints.DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(x => x.RoutePattern.RawText)
            .ToList();

        returned.Should().BeSameAs(app);
        routePatterns.Should().Contain("/api/chat");
        routePatterns.Should().Contain("/api/commands");
        routePatterns.Should().Contain("/api/agents");
        routePatterns.Should().Contain("/api/workflows");
        routePatterns.Should().Contain("/api/ws/chat");
        routePatterns.Should().Contain("/api/actors/{actorId}");
        routePatterns.Should().Contain("/api/actors/{actorId}/timeline");
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

        await ChatEndpoints.HandleChat(
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
                    Type = "RUN_STARTED",
                    ThreadId = "actor-1",
                }, ct);

                await emitAsync(new WorkflowOutputFrame
                {
                    Type = "RUN_FINISHED",
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

        await ChatEndpoints.HandleChat(
            http,
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            service,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var body = await ReadBodyAsync(http);
        body.Should().Contain("data:");
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_FINISHED");
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

        var result = await ChatEndpoints.HandleCommand(
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

        public bool ActorQueryEnabled => ActorQueryEnabledValue;

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult(Agents);

        public IReadOnlyList<string> ListWorkflows() => Workflows;

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
    }

    private static CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> ToCoreResult(
        WorkflowChatRunExecutionResult source) =>
        new(source.Error, source.Started, source.FinalizeResult);
}
