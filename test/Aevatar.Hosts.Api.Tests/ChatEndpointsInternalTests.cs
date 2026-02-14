using System.Reflection;
using System.Text.Json;
using Aevatar.CQRS.Projections.Abstractions;
using Aevatar.CQRS.Projections.Abstractions.ReadModels;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Hosts.Api.Endpoints;
using Aevatar.Hosts.Api.Workflows;
using Aevatar.Workflows.Core;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.Hosts.Api.Tests;

public class ChatEndpointsInternalTests
{
    [Fact]
    public void MapChatEndpoints_ShouldRegisterCoreRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IActorRuntime>(new FakeActorRuntime([]));
        builder.Services.AddSingleton<IStreamProvider>(new InMemoryStreamProvider());
        builder.Services.AddSingleton(new WorkflowRegistry());
        builder.Services.AddSingleton<IWorkflowExecutionProjectionService>(
            new FakeProjectionService(enableRunQueryEndpoints: true, enableRunReportArtifacts: false));
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
    public async Task ListWorkflows_ShouldReturnWorkflowNames()
    {
        var registry = new WorkflowRegistry();
        registry.Register("custom", "name: custom");

        var result = InvokeSync<IResult>("ListWorkflows", registry);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.EnumerateArray().Select(x => x.GetString()).Should().Contain("custom");
    }

    [Fact]
    public async Task ListRuns_ShouldReturnProjectedRunSummary()
    {
        var report = new WorkflowExecutionReport
        {
            RunId = "run-1",
            WorkflowName = "direct",
            RootActorId = "root-1",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2),
            EndedAt = DateTimeOffset.UtcNow,
            DurationMs = 123,
            Success = true,
            Summary = new WorkflowExecutionSummary { TotalSteps = 3 },
        };
        var projectionService = new FakeProjectionService([report]);

        var result = await InvokeTask<IResult>(
            "ListRuns",
            projectionService,
            50,
            CancellationToken.None);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(1);
        doc.RootElement[0].GetProperty("runId").GetString().Should().Be("run-1");
        doc.RootElement[0].GetProperty("workflowName").GetString().Should().Be("direct");
        doc.RootElement[0].GetProperty("totalSteps").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task GetRun_WhenMissing_ShouldReturnNotFound()
    {
        var projectionService = new FakeProjectionService([]);

        var result = await InvokeTask<IResult>(
            "GetRun",
            "missing",
            projectionService,
            CancellationToken.None);
        var (statusCode, _) = await ExecuteResultAsync(result);

        statusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task GetRun_WhenExists_ShouldReturnOk()
    {
        var projectionService = new FakeProjectionService(
        [
            new WorkflowExecutionReport { RunId = "run-2", WorkflowName = "direct", RootActorId = "root-2" },
        ]);

        var result = await InvokeTask<IResult>(
            "GetRun",
            "run-2",
            projectionService,
            CancellationToken.None);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.GetProperty("runId").GetString().Should().Be("run-2");
    }

    [Fact]
    public async Task ListAgents_ShouldReturnAgentMetadata()
    {
        var runtime = new FakeActorRuntime(
        [
            new FakeActor("a-1", parentId: null, new FakeAgent("agent-1", "desc-1")),
            new FakeActor("a-2", parentId: "a-1", new FakeAgent("agent-2", "desc-2")),
        ]);

        var result = await InvokeTask<IResult>("ListAgents", runtime);
        var (statusCode, body) = await ExecuteResultAsync(result);
        using var doc = JsonDocument.Parse(body);

        statusCode.Should().Be(StatusCodes.Status200OK);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement[0].GetProperty("id").GetString().Should().Be("a-1");
        doc.RootElement[0].GetProperty("description").GetString().Should().Be("desc-1");
    }

    [Fact]
    public async Task BuildTopologyAsync_ShouldOnlyReturnReachableEdgesFromRoot()
    {
        var runtime = new FakeActorRuntime(
        [
            new FakeActor("root", null, new FakeAgent("a-root", "root")),
            new FakeActor("child-1", "root", new FakeAgent("a-1", "child-1")),
            new FakeActor("child-2", "child-1", new FakeAgent("a-2", "child-2")),
            new FakeActor("orphan", "unknown-parent", new FakeAgent("a-3", "orphan")),
        ]);

        var topology = await InvokeTask<List<WorkflowExecutionTopologyEdge>>(
            "BuildTopologyAsync",
            runtime,
            "root");

        topology.Should().HaveCount(2);
        topology.Should().Contain(new WorkflowExecutionTopologyEdge("root", "child-1"));
        topology.Should().Contain(new WorkflowExecutionTopologyEdge("child-1", "child-2"));
        topology.Should().NotContain(x => x.Parent == "unknown-parent");
    }

    [Fact]
    public async Task BuildTopologyAsync_WhenRootEmpty_ShouldReturnEmpty()
    {
        var runtime = new FakeActorRuntime(
        [
            new FakeActor("child-1", "root", new FakeAgent("a-1", "child-1")),
        ]);

        var topology = await InvokeTask<List<WorkflowExecutionTopologyEdge>>(
            "BuildTopologyAsync",
            runtime,
            "");

        topology.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleChat_WhenAgentIdMissing_ShouldReturn404()
    {
        var http = CreateHttpContext();
        var runtime = new FakeActorRuntime([]);
        var streams = new InMemoryStreamProvider();
        var registry = new WorkflowRegistry();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var projectionService = new FakeProjectionService(enableRunQueryEndpoints: false, enableRunReportArtifacts: false);

        await InvokeVoidTask(
            "HandleChat",
            http,
            new ChatInput { Prompt = "hello", AgentId = "missing" },
            runtime,
            streams,
            registry,
            loggerFactory,
            projectionService,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleChat_WhenWorkflowMissing_ShouldReturn404()
    {
        var http = CreateHttpContext();
        var runtime = new FakeActorRuntime([]);
        var streams = new InMemoryStreamProvider();
        var registry = new WorkflowRegistry();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var projectionService = new FakeProjectionService(enableRunQueryEndpoints: false, enableRunReportArtifacts: false);

        await InvokeVoidTask(
            "HandleChat",
            http,
            new ChatInput { Prompt = "hello", Workflow = "not-found" },
            runtime,
            streams,
            registry,
            loggerFactory,
            projectionService,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleChat_WhenSucceeded_ShouldWriteRunStartedAndRunFinished()
    {
        var http = CreateHttpContext();
        var streams = new InMemoryStreamProvider();
        var runtime = new FakeActorRuntime(
            [],
            createFactory: actorId => new FakeActor(
                actorId,
                parentId: null,
                new FakeAgent("agent-" + actorId, "ok"),
                async (_, _) =>
                {
                    await streams.GetStream(actorId).ProduceAsync(Wrap(new StartWorkflowEvent
                    {
                        WorkflowName = "direct",
                        RunId = "wf-run-1",
                        Input = "hello",
                    }));
                    await streams.GetStream(actorId).ProduceAsync(Wrap(new WorkflowCompletedEvent
                    {
                        WorkflowName = "direct",
                        RunId = "wf-run-1",
                        Success = true,
                        Output = "done",
                    }));
                }));
        var registry = new WorkflowRegistry();
        registry.Register("direct", WorkflowRegistry.BuiltInDirectYaml);
        var loggerFactory = LoggerFactory.Create(_ => { });
        var projectionService = new FakeProjectionService(
            enableRunQueryEndpoints: false,
            enableRunReportArtifacts: false,
            waitForRunCompletionResult: true);

        await InvokeVoidTask(
            "HandleChat",
            http,
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            runtime,
            streams,
            registry,
            loggerFactory,
            projectionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"type\":\"RUN_STARTED\"");
        body.Should().Contain("\"type\":\"RUN_FINISHED\"");
        projectionService.StartCalls.Should().Be(1);
        projectionService.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task HandleChat_WhenActorThrows_ShouldWriteRunError()
    {
        var http = CreateHttpContext();
        var streams = new InMemoryStreamProvider();
        var runtime = new FakeActorRuntime(
            [],
            createFactory: actorId => new FakeActor(
                actorId,
                parentId: null,
                new FakeAgent("agent-" + actorId, "fail"),
                (_, _) => throw new InvalidOperationException("boom")));
        var registry = new WorkflowRegistry();
        registry.Register("direct", WorkflowRegistry.BuiltInDirectYaml);
        var loggerFactory = LoggerFactory.Create(_ => { });
        var projectionService = new FakeProjectionService(
            enableRunQueryEndpoints: false,
            enableRunReportArtifacts: false,
            waitForRunCompletionResult: false);

        await InvokeVoidTask(
            "HandleChat",
            http,
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            runtime,
            streams,
            registry,
            loggerFactory,
            projectionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"type\":\"RUN_ERROR\"");
        body.Should().Contain("\"code\":\"INTERNAL_ERROR\"");
        projectionService.StartCalls.Should().Be(1);
        projectionService.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task HandleChat_WhenExistingAgentProvided_ShouldReuseAgentWithoutCreate()
    {
        var http = CreateHttpContext();
        var streams = new InMemoryStreamProvider();
        var existingActor = new FakeActor(
            "actor-existing",
            parentId: null,
            new FakeAgent("agent-existing", "ok"),
            async (_, _) =>
            {
                await streams.GetStream("actor-existing").ProduceAsync(Wrap(new WorkflowCompletedEvent
                {
                    WorkflowName = "direct",
                    RunId = "wf-run-existing",
                    Success = true,
                    Output = "done",
                }));
            });
        var runtime = new FakeActorRuntime([existingActor]);
        var registry = new WorkflowRegistry();
        var loggerFactory = LoggerFactory.Create(_ => { });
        var projectionService = new FakeProjectionService(enableRunQueryEndpoints: false, enableRunReportArtifacts: false);

        await InvokeVoidTask(
            "HandleChat",
            http,
            new ChatInput { Prompt = "hello", AgentId = "actor-existing" },
            runtime,
            streams,
            registry,
            loggerFactory,
            projectionService,
            CancellationToken.None);

        var body = await ReadBodyAsync(http);
        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        body.Should().Contain("\"type\":\"RUN_FINISHED\"");
        runtime.CreateCalls.Should().Be(0);
    }

    [Fact]
    public async Task HandleChat_WhenCompleteReturnsNullAndQueryEnabled_ShouldFallbackToGetRun()
    {
        var http = CreateHttpContext();
        var streams = new InMemoryStreamProvider();
        var runtime = new FakeActorRuntime(
            [],
            createFactory: actorId => new FakeActor(
                actorId,
                parentId: null,
                new FakeAgent("agent-" + actorId, "ok"),
                async (_, _) =>
                {
                    await streams.GetStream(actorId).ProduceAsync(Wrap(new WorkflowCompletedEvent
                    {
                        WorkflowName = "direct",
                        RunId = "wf-run-query",
                        Success = true,
                        Output = "done",
                    }));
                }));
        var registry = new WorkflowRegistry();
        registry.Register("direct", WorkflowRegistry.BuiltInDirectYaml);
        var loggerFactory = LoggerFactory.Create(_ => { });
        var projectionService = new FakeProjectionService(
            reports:
            [
                new WorkflowExecutionReport
                {
                    RunId = "run-1",
                    WorkflowName = "direct",
                    RootActorId = "actor-1",
                },
            ],
            enableRunQueryEndpoints: true,
            enableRunReportArtifacts: false,
            waitForRunCompletionResult: true,
            completeResult: null);

        await InvokeVoidTask(
            "HandleChat",
            http,
            new ChatInput { Prompt = "hello", Workflow = "direct" },
            runtime,
            streams,
            registry,
            loggerFactory,
            projectionService,
            CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        projectionService.GetRunCalls.Should().Be(1);
    }

    private static T InvokeSync<T>(string methodName, params object?[] args)
    {
        var method = typeof(ChatEndpoints).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull($"private method {methodName} should exist");
        return (T)method!.Invoke(null, args)!;
    }

    private static async Task<T> InvokeTask<T>(string methodName, params object?[] args)
    {
        var method = typeof(ChatEndpoints).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull($"private method {methodName} should exist");
        var task = method!.Invoke(null, args).Should().BeAssignableTo<Task>().Subject;
        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        resultProperty.Should().NotBeNull();
        return (T)resultProperty!.GetValue(task)!;
    }

    private static async Task InvokeVoidTask(string methodName, params object?[] args)
    {
        var method = typeof(ChatEndpoints).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull($"private method {methodName} should exist");
        var task = method!.Invoke(null, args).Should().BeAssignableTo<Task>().Subject;
        await task;
    }

    private static DefaultHttpContext CreateHttpContext()
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
            .AddRouting()
            .BuildServiceProvider();
        return http;
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

    private static EventEnvelope Wrap(IMessage evt, string publisherId = "root") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Payload = Any.Pack(evt),
        PublisherId = publisherId,
        Direction = EventDirection.Self,
    };

    private sealed class FakeProjectionService : IWorkflowExecutionProjectionService
    {
        private readonly IReadOnlyList<WorkflowExecutionReport> _reports;
        private readonly bool _enableRunQueryEndpoints;
        private readonly bool _enableRunReportArtifacts;
        private readonly bool _waitForRunCompletionResult;
        private readonly WorkflowExecutionReport? _completeResult;
        private int _runSeed;

        public int StartCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public int GetRunCalls { get; private set; }

        public FakeProjectionService(
            IReadOnlyList<WorkflowExecutionReport>? reports = null,
            bool enableRunQueryEndpoints = true,
            bool enableRunReportArtifacts = true,
            bool waitForRunCompletionResult = true,
            WorkflowExecutionReport? completeResult = null)
        {
            _reports = reports ?? [];
            _enableRunQueryEndpoints = enableRunQueryEndpoints;
            _enableRunReportArtifacts = enableRunReportArtifacts;
            _waitForRunCompletionResult = waitForRunCompletionResult;
            _completeResult = completeResult;
        }

        public bool ProjectionEnabled => true;
        public bool EnableRunQueryEndpoints => _enableRunQueryEndpoints;
        public bool EnableRunReportArtifacts => _enableRunReportArtifacts;

        public Task<WorkflowExecutionProjectionSession> StartAsync(string rootActorId, string workflowName, string input, CancellationToken ct = default)
        {
            StartCalls++;
            var startedAt = DateTimeOffset.UtcNow;
            var runId = "run-" + Interlocked.Increment(ref _runSeed);
            return Task.FromResult(new WorkflowExecutionProjectionSession
            {
                RunId = runId,
                StartedAt = startedAt,
                Context = new WorkflowExecutionProjectionContext
                {
                    RunId = runId,
                    RootActorId = rootActorId,
                    WorkflowName = workflowName,
                    StartedAt = startedAt,
                    Input = input,
                },
            });
        }

        public Task ProjectAsync(WorkflowExecutionProjectionSession session, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> WaitForRunProjectionCompletedAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(_waitForRunCompletionResult);

        public Task<WorkflowExecutionReport?> CompleteAsync(WorkflowExecutionProjectionSession session, IReadOnlyList<WorkflowExecutionTopologyEdge> topology, CancellationToken ct = default)
        {
            CompleteCalls++;
            return Task.FromResult(_completeResult);
        }

        public Task<IReadOnlyList<WorkflowExecutionReport>> ListRunsAsync(int take = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkflowExecutionReport>>(_reports.Take(take).ToList());

        public Task<WorkflowExecutionReport?> GetRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(GetRunImpl(runId));

        private WorkflowExecutionReport? GetRunImpl(string runId)
        {
            GetRunCalls++;
            return _reports.FirstOrDefault(x => string.Equals(x.RunId, runId, StringComparison.Ordinal));
        }
    }

    private sealed class FakeActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors;
        private readonly Func<string, IActor>? _createFactory;
        private int _idSeed;
        public int CreateCalls { get; private set; }

        public FakeActorRuntime(IReadOnlyList<IActor> actors, Func<string, IActor>? createFactory = null)
        {
            _actors = actors.ToDictionary(x => x.Id, StringComparer.Ordinal);
            _createFactory = createFactory;
            _idSeed = _actors.Count;
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateInternal(id);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateInternal(id);

        public Task DestroyAsync(string id, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<IReadOnlyList<IActor>> GetAllAsync() =>
            Task.FromResult<IReadOnlyList<IActor>>(_actors.Values.ToList());

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task UnlinkAsync(string childId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task RestoreAllAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();

        private Task<IActor> CreateInternal(string? id)
        {
            if (_createFactory == null)
                throw new NotImplementedException();

            CreateCalls++;
            var actorId = string.IsNullOrWhiteSpace(id)
                ? "actor-" + Interlocked.Increment(ref _idSeed)
                : id;
            var actor = _createFactory(actorId);
            _actors[actor.Id] = actor;
            return Task.FromResult(actor);
        }
    }

    private sealed class FakeActor(
        string id,
        string? parentId,
        IAgent agent,
        Func<EventEnvelope, CancellationToken, Task>? handle = null) : IActor
    {
        private readonly Func<EventEnvelope, CancellationToken, Task>? _handle = handle;

        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) =>
            _handle?.Invoke(envelope, ct) ?? Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult(parentId);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeAgent(string id, string description) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(description);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
