using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Queries;
using Aevatar.Host.Api.Endpoints;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Host.Api.Tests;

public class ChatEndpointsInternalTests
{
    [Fact]
    public void MapChatEndpoints_ShouldRegisterCoreRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ICommandExecutionService<WorkflowChatRunRequest, WorkflowChatRunStarted, WorkflowOutputFrame, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError>>(new FakeChatRunApplicationService());
        var queryService = new FakeQueryService { RunQueryEnabledValue = true };
        builder.Services.AddSingleton<IAgentQueryService<WorkflowAgentSummary>>(queryService);
        builder.Services.AddSingleton<IExecutionTemplateQueryService>(queryService);
        builder.Services.AddSingleton<IExecutionQueryService<WorkflowRunSummary, WorkflowRunReport>>(queryService);

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
        routePatterns.Should().Contain("/api/agents");
        routePatterns.Should().Contain("/api/workflows");
        routePatterns.Should().Contain("/api/ws/chat");
        routePatterns.Should().Contain("/api/runs");
        routePatterns.Should().Contain("/api/runs/{runId}");
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
                var started = new WorkflowChatRunStarted("actor-1", "direct", "run-1");
                if (onStartedAsync != null)
                    await onStartedAsync(started, ct);

                await emitAsync(new WorkflowOutputFrame
                {
                    Type = "RUN_STARTED",
                    RunId = "run-1",
                    ThreadId = "actor-1",
                }, ct);

                await emitAsync(new WorkflowOutputFrame
                {
                    Type = "RUN_FINISHED",
                    RunId = "run-1",
                    ThreadId = "actor-1",
                }, ct);

                return ToCoreResult(
                    new WorkflowChatRunExecutionResult(
                        WorkflowChatRunStartError.None,
                        started,
                        new WorkflowChatRunFinalizeResult(
                            WorkflowProjectionCompletionStatus.Completed,
                            true,
                            new WorkflowRunReport
                            {
                                RunId = "run-1",
                                WorkflowName = "direct",
                                RootActorId = "actor-1",
                            })));
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
    public async Task ListRuns_ShouldReturnRunSummaries()
    {
        var queryService = new FakeQueryService
        {
            RunQueryEnabledValue = true,
            Runs =
            [
                new WorkflowRunSummary(
                    "run-1",
                    "direct",
                    "actor-1",
                    DateTimeOffset.UtcNow.AddSeconds(-1),
                    DateTimeOffset.UtcNow,
                    200,
                    true,
                    3,
                    WorkflowRunProjectionScope.ActorShared,
                    WorkflowRunCompletionStatus.Completed),
            ],
        };

        var result = await ChatQueryEndpoints.ListRuns(queryService, 50, CancellationToken.None);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("runId").GetString().Should().Be("run-1");
        doc.RootElement[0].GetProperty("totalSteps").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task GetRun_WhenMissing_ShouldReturnNotFound()
    {
        var queryService = new FakeQueryService { RunQueryEnabledValue = true };

        var result = await ChatQueryEndpoints.GetRun("missing", queryService, CancellationToken.None);
        var (statusCode, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status404NotFound);
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
        IAgentQueryService<WorkflowAgentSummary>,
        IExecutionTemplateQueryService,
        IExecutionQueryService<WorkflowRunSummary, WorkflowRunReport>
    {
        public bool RunQueryEnabledValue { get; set; }
        public IReadOnlyList<WorkflowAgentSummary> Agents { get; set; } = [];
        public IReadOnlyList<string> Workflows { get; set; } = [];
        public IReadOnlyList<WorkflowRunSummary> Runs { get; set; } = [];
        public WorkflowRunReport? Report { get; set; }

        public bool ExecutionQueryEnabled => RunQueryEnabledValue;

        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult(Agents);

        public IReadOnlyList<string> ListTemplates() => Workflows;

        public Task<IReadOnlyList<WorkflowRunSummary>> ListAsync(int take = 50, CancellationToken ct = default) =>
            Task.FromResult(Runs);

        public Task<WorkflowRunReport?> GetAsync(string executionId, CancellationToken ct = default) =>
            Task.FromResult(Report is { RunId: var id } && string.Equals(id, executionId, StringComparison.Ordinal) ? Report : null);
    }

    private static CommandExecutionResult<WorkflowChatRunStarted, WorkflowChatRunFinalizeResult, WorkflowChatRunStartError> ToCoreResult(
        WorkflowChatRunExecutionResult source) =>
        new(source.Error, source.Started, source.FinalizeResult);
}
