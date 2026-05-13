using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class ResponsesAgentToolStateCommandAdapterTests
{
    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();
        var projection = new RecordingProjectionPort();

        ((Action)(() => new ResponsesAgentToolStateCommandAdapter(null!, dispatch, projection)))
            .Should().Throw<ArgumentNullException>().WithMessage("*runtime*");
        ((Action)(() => new ResponsesAgentToolStateCommandAdapter(runtime, null!, projection)))
            .Should().Throw<ArgumentNullException>().WithMessage("*dispatchPort*");
        ((Action)(() => new ResponsesAgentToolStateCommandAdapter(runtime, dispatch, null!)))
            .Should().Throw<ArgumentNullException>().WithMessage("*projectionPort*");
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldDispatchAndPreviewArrayItems()
    {
        var (adapter, _, dispatch, projection) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync(
            scopeId: "scope-1",
            ownerSubject: "owner-1",
            sourceResponseId: "resp_1",
            argumentsJson: """{"todos":[{"id":"todo-1","content":"Ship","status":"in_progress"},{"content":"Review"}]}""");

        result.SourceResponseId.Should().Be("resp_1");
        result.Todos.Should().HaveCount(2);
        result.Todos[0].Id.Should().Be("todo-1");
        result.Todos[0].Status.Should().Be("in_progress");
        result.Todos[1].Id.Should().Be("todo_0001");
        result.Todos[1].Status.Should().Be("pending");

        // EnsureActorAsync registers + projection-ensures + dispatches
        projection.EnsureCalls.Should().ContainSingle();
        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls[1].envelope.Payload.TypeUrl.Should().Contain("ApplyResponsesTodoWriteRequested");
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<ApplyResponsesTodoWriteRequested>();
        ResponsesJsonValues.ToBoundaryJson(packed.Arguments)
            .Should().Be("""{"todos":[{"id":"todo-1","content":"Ship","status":"in_progress"},{"content":"Review"}]}""");
        packed.TodoItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldHandleSingleStringTodo()
    {
        var (adapter, _, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1",
            """ "just a single content string" """);

        result.Todos.Should().ContainSingle();
        result.Todos[0].Content.Should().Be("just a single content string");
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldHandleSingleObjectTodo_FallbackContentKey()
    {
        var (adapter, _, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1",
            """{"task":"do thing"}""");

        result.Todos.Should().ContainSingle();
        result.Todos[0].Content.Should().Be("do thing");
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldReturnEmpty_OnMalformedJson()
    {
        var (adapter, _, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1", "not json {");

        result.Todos.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldReturnEmpty_OnNullArguments()
    {
        var (adapter, _, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1", argumentsJson: null!);

        result.Todos.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldSkipObjectsWithoutContent()
    {
        var (adapter, _, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1",
            """{"todos":[{"id":"x"},{"content":"keep"}]}""");

        result.Todos.Should().ContainSingle();
        result.Todos[0].Content.Should().Be("keep");
    }

    [Theory]
    [InlineData("", "owner-1", "scopeId")]
    [InlineData("scope-1", "", "ownerSubject")]
    public async Task ApplyTodoWriteAsync_ShouldRejectMissingActorIdentity(string scope, string owner, string param)
    {
        var (adapter, _, _, _) = CreateAdapter();

        var act = () => adapter.ApplyTodoWriteAsync(scope, owner, "resp_1", "{}");

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    [Theory]
    [InlineData("""{"description":"do alpha"}""", "do alpha")]
    [InlineData("""{"prompt":"do beta"}""", "do beta")]
    [InlineData("""{"task":"do gamma"}""", "do gamma")]
    [InlineData("""{"input":"do delta"}""", "do delta")]
    public async Task RecordTaskAsync_ShouldExtractDescription_FromKnownKeys(string args, string expected)
    {
        var (adapter, _, dispatch, _) = CreateAdapter();

        var result = await adapter.RecordTaskAsync("scope-1", "owner-1", "resp_1", args);

        result.Status.Should().Be("accepted");
        result.TaskId.Should().StartWith("task_");
        result.ChildActorId.Should().Contain("-task-");
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesTaskRequested>();
        packed.Description.Should().Be(expected);
        ResponsesJsonValues.ToBoundaryJson(packed.Arguments).Should().Be(args);
        ResponsesJsonValues.ToBoundaryJson(packed.Result).Should().Contain("\"status\":\"accepted\"");
    }

    [Fact]
    public async Task RecordTaskAsync_ShouldFallBackToRawArguments_OnNonObjectJson()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();

        await adapter.RecordTaskAsync("scope-1", "owner-1", "resp_1", "[1,2,3]");

        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesTaskRequested>();
        packed.Description.Should().Be("[1,2,3]");
    }

    [Fact]
    public async Task RecordTaskAsync_ShouldFallBackToRawArguments_OnMalformedJson()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();

        await adapter.RecordTaskAsync("scope-1", "owner-1", "resp_1", "{not json");

        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesTaskRequested>();
        packed.Description.Should().Be("{not json");
    }

    [Fact]
    public async Task RecordTaskAsync_ShouldReturnEmptyDescription_OnEmptyArguments()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();

        await adapter.RecordTaskAsync("scope-1", "owner-1", "resp_1", argumentsJson: "");

        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesTaskRequested>();
        packed.Description.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordWebTraceAsync_ShouldRejectNullTrace()
    {
        var (adapter, _, _, _) = CreateAdapter();

        await ((Func<Task>)(() => adapter.RecordWebTraceAsync("scope-1", "owner-1", "resp_1", null!)))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RecordWebTraceAsync_ShouldReuseProvidedTraceId()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();
        var trace = new ResponsesWebTraceInput(
            TraceId: "web_explicit",
            ToolName: "WebFetch",
            CacheKey: "cache-1",
            Url: "https://example.com",
            Query: string.Empty,
            CacheHit: false,
            ResultJson: """{"content":"x"}""");

        var result = await adapter.RecordWebTraceAsync("scope-1", "owner-1", "resp_1", trace);

        result.TraceId.Should().Be("web_explicit");
        result.CacheHit.Should().BeFalse();
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesWebTraceRequested>();
        packed.TraceId.Should().Be("web_explicit");
        ResponsesJsonValues.ToBoundaryJson(packed.Result).Should().Be("""{"content":"x"}""");
    }

    [Fact]
    public async Task RecordWebTraceAsync_ShouldGenerateTraceId_WhenMissing()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();
        var trace = new ResponsesWebTraceInput(
            TraceId: string.Empty,
            ToolName: "WebSearch",
            CacheKey: "cache-2",
            Url: string.Empty,
            Query: "weather",
            CacheHit: true,
            ResultJson: string.Empty);

        var result = await adapter.RecordWebTraceAsync("scope-1", "owner-1", "resp_1", trace);

        result.TraceId.Should().StartWith("web_");
        result.CacheHit.Should().BeTrue();
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesWebTraceRequested>();
        packed.TraceId.Should().Be(result.TraceId);
        ResponsesJsonValues.ToBoundaryJson(packed.Result).Should().Be("{}");
    }

    private static (ResponsesAgentToolStateCommandAdapter adapter, RecordingRuntime runtime, RecordingDispatchPort dispatch, RecordingProjectionPort projection) CreateAdapter()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();
        var projection = new RecordingProjectionPort();
        var adapter = new ResponsesAgentToolStateCommandAdapter(runtime, dispatch, projection);
        return (adapter, runtime, dispatch, projection);
    }

    private sealed class RecordingRuntime : IActorRuntime
    {
        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            Task.FromResult<IActor>(new RecordingActor(id ?? $"created:{agentType.Name}"));

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProjectionPort : IResponsesAgentToolStateCurrentStateProjectionPort
    {
        public List<string> EnsureCalls { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            EnsureCalls.Add(actorId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id) { Id = id; }
        public string Id { get; }
        public IAgent Agent { get; } = new TestStaticServiceAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
