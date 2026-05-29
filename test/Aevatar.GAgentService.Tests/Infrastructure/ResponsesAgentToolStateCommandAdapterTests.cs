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

        ((Action)(() => new ResponsesAgentToolStateCommandAdapter(null!, dispatch)))
            .Should().Throw<ArgumentNullException>().WithMessage("*runtime*");
        ((Action)(() => new ResponsesAgentToolStateCommandAdapter(runtime, null!)))
            .Should().Throw<ArgumentNullException>().WithMessage("*dispatchPort*");
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldRegisterActorAndDispatchWithoutProjectionEnsure()
    {
        var (adapter, _, dispatch) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync(
            scopeId: "scope-1",
            ownerSubject: "owner-1",
            sourceResponseId: "resp_1",
            argumentsJson: """{"todos":[{"id":"todo-1","content":"Ship","status":"in_progress"},{"content":"Review"}]}""");

        result.SourceResponseId.Should().Be("resp_1");
        result.Todos.Should().HaveCount(2);
        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls[0].envelope.Payload.TypeUrl.Should().Contain("RegisterResponsesAgentToolStateRequested");
        dispatch.Calls[1].envelope.Payload.TypeUrl.Should().Contain("ApplyResponsesTodoWriteRequested");
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<ApplyResponsesTodoWriteRequested>();
        ResponsesJsonValues.ToBoundaryJson(packed.Arguments)
            .Should().Be("""{"todos":[{"id":"todo-1","content":"Ship","status":"in_progress"},{"content":"Review"}]}""");
        packed.TodoItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldUseReadableActorId()
    {
        var (adapter, runtime, dispatch) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync(
            scopeId: " scope:tenant/1 ",
            ownerSubject: " user@example.com/sub ",
            sourceResponseId: "resp_1",
            argumentsJson: """{"todos":[{"content":"Ship"}]}""");

        var actorId = ResponseAgentToolStateIds.BuildActorId("scope:tenant/1", "user@example.com/sub");
        actorId.Should().Be("responses-agent-tools-scope:scope%3Atenant%2F1|owner:user%40example.com%2Fsub");
        result.ActorId.Should().Be(actorId);
        runtime.CreateCalls.Should().ContainSingle(call => call.id == actorId);
        dispatch.Calls.Should().OnlyContain(call => call.actorId == actorId);
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldResolveExistingLegacyActorId()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();
        var adapter = new ResponsesAgentToolStateCommandAdapter(runtime, dispatch);
        var legacyActorId = ResponseAgentToolStateIds.BuildLegacyActorId("scope-1", "owner-1");
        runtime.ExistingActorIds.Add(legacyActorId);

        var result = await adapter.ApplyTodoWriteAsync(
            scopeId: "scope-1",
            ownerSubject: "owner-1",
            sourceResponseId: "resp_1",
            argumentsJson: """{"todos":[{"content":"Ship"}]}""");

        result.ActorId.Should().Be(legacyActorId);
        legacyActorId.Should().MatchRegex("^responses-agent-tools-[0-9a-f]{32}$");
        runtime.CreateCalls.Should().ContainSingle(call => call.id == legacyActorId);
        dispatch.Calls.Should().OnlyContain(call => call.actorId == legacyActorId);
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldHandleSingleStringTodo()
    {
        var (adapter, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1",
            """ "just a single content string" """);

        result.Todos.Should().ContainSingle();
        result.Todos[0].Content.Should().Be("just a single content string");
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldHandleSingleObjectTodo_FallbackContentKey()
    {
        var (adapter, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1",
            """{"task":"do thing"}""");

        result.Todos.Should().ContainSingle();
        result.Todos[0].Content.Should().Be("do thing");
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldReturnEmpty_OnMalformedJson()
    {
        var (adapter, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1", "not json {");

        result.Todos.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldReturnEmpty_OnNullArguments()
    {
        var (adapter, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1", argumentsJson: null!);

        result.Todos.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldSkipObjectsWithoutContent()
    {
        var (adapter, _, _) = CreateAdapter();

        var result = await adapter.ApplyTodoWriteAsync("scope-1", "owner-1", "resp_1",
            """{"todos":[{"id":"x"},{"content":"keep"}]}""");

        result.Todos.Should().ContainSingle();
        result.Todos[0].Content.Should().Be("keep");
    }

    [Fact]
    public async Task RecordWebTraceAsync_ShouldDispatchTrace()
    {
        var (adapter, _, dispatch) = CreateAdapter();
        var trace = new ResponsesWebTraceInput(
            TraceId: "web_explicit",
            ToolName: "WebFetch",
            CacheKey: "cache-1",
            Url: "https://example.com",
            Query: string.Empty,
            CacheHit: false,
            Result: ResponsesWebResultMigration.FromFetch(new ResponsesWebFetchToolOutput
            {
                Url = "https://example.com",
                Content = "x",
            }));

        var result = await adapter.RecordWebTraceAsync("scope-1", "owner-1", "resp_1", trace);

        result.TraceId.Should().Be("web_explicit");
        dispatch.Calls.Should().HaveCount(2);
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesWebTraceRequested>();
        packed.TraceId.Should().Be("web_explicit");
        packed.TypedResult.Fetch.Content.Should().Be("x");
        ResponsesJsonValues.ToBoundaryJson(packed.Result).Should().Be(
            """{"url":"https://example.com","status_code":0,"content_type":"","content":"x"}""");
    }

    [Fact]
    public async Task RecordWebTraceAsync_ShouldGenerateTraceId_WhenMissing()
    {
        var (adapter, _, dispatch) = CreateAdapter();
        var trace = new ResponsesWebTraceInput(
            TraceId: string.Empty,
            ToolName: "WebSearch",
            CacheKey: "cache-2",
            Url: string.Empty,
            Query: "weather",
            CacheHit: true,
            Result: new ResponsesWebToolResult());

        var result = await adapter.RecordWebTraceAsync("scope-1", "owner-1", "resp_1", trace);

        result.TraceId.Should().StartWith("web_");
        result.CacheHit.Should().BeTrue();
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesWebTraceRequested>();
        packed.TraceId.Should().Be(result.TraceId);
        packed.TypedResult.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.None);
        packed.Result.Should().NotBeNull();
        packed.Result.KindCase.Should().Be(Google.Protobuf.WellKnownTypes.Value.KindOneofCase.None);
    }

    [Fact]
    public async Task ApplyTodoWriteAsync_ShouldRejectMissingActorIdentity()
    {
        var (adapter, _, _) = CreateAdapter();

        await ((Func<Task>)(() => adapter.ApplyTodoWriteAsync("", "owner-1", "resp_1", "{}")))
            .Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == "scopeId");
        await ((Func<Task>)(() => adapter.ApplyTodoWriteAsync("scope-1", "", "resp_1", "{}")))
            .Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == "ownerSubject");
    }

    [Fact]
    public async Task RecordWebTraceAsync_ShouldRejectNullTrace()
    {
        var (adapter, _, _) = CreateAdapter();

        await ((Func<Task>)(() => adapter.RecordWebTraceAsync("scope-1", "owner-1", "resp_1", null!)))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void AdapterSources_ShouldNotContainProjectionActivationCalls()
    {
        var sources = new[]
        {
            SourcePath("src/platform/Aevatar.GAgentService.Infrastructure/Adapters/LlmSessionRegistrationAdapter.cs"),
            SourcePath("src/platform/Aevatar.GAgentService.Infrastructure/Adapters/ResponsesAgentToolStateCommandAdapter.cs"),
            SourcePath("src/platform/Aevatar.GAgentService.Infrastructure/Adapters/ServiceRunRegistrationAdapter.cs"),
        };

        foreach (var sourcePath in sources)
        {
            var source = File.ReadAllText(sourcePath);
            source.Should().NotContain("EnsureProjectionAsync");
            source.Should().NotContain("ActivateAsync(");
        }
    }

    private static (ResponsesAgentToolStateCommandAdapter adapter, RecordingRuntime runtime, RecordingDispatchPort dispatch) CreateAdapter()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();
        var adapter = new ResponsesAgentToolStateCommandAdapter(runtime, dispatch);
        return (adapter, runtime, dispatch);
    }

    private static string SourcePath(string relativePath)
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Could not locate source file '{relativePath}'.");
    }

    private sealed class RecordingRuntime : IActorRuntime
    {
        public HashSet<string> ExistingActorIds { get; } = new(StringComparer.Ordinal);
        public List<(System.Type agentType, string? id)> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            CreateCalls.Add((agentType, id));
            if (!string.IsNullOrWhiteSpace(id))
                ExistingActorIds.Add(id);

            return Task.FromResult<IActor>(new RecordingActor(id ?? $"created:{agentType.Name}"));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(
            ExistingActorIds.Contains(id) ? new RecordingActor(id) : null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActorIds.Contains(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
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
