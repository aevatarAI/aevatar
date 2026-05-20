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
    public async Task RecordTaskAsync_ShouldExtractDescriptionAndDispatch()
    {
        var (adapter, _, dispatch) = CreateAdapter();

        var result = await adapter.RecordTaskAsync(
            "scope-1",
            "owner-1",
            "resp_1",
            """{"description":"do alpha"}""");

        result.Status.Should().Be("accepted");
        result.TaskId.Should().StartWith("task_");
        dispatch.Calls.Should().HaveCount(2);
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesTaskRequested>();
        packed.Description.Should().Be("do alpha");
        ResponsesJsonValues.ToBoundaryJson(packed.Arguments).Should().Be("""{"description":"do alpha"}""");
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
            ResultJson: """{"content":"x"}""");

        var result = await adapter.RecordWebTraceAsync("scope-1", "owner-1", "resp_1", trace);

        result.TraceId.Should().Be("web_explicit");
        dispatch.Calls.Should().HaveCount(2);
        var packed = dispatch.Calls[1].envelope.Payload.Unpack<RecordResponsesWebTraceRequested>();
        packed.TraceId.Should().Be("web_explicit");
        ResponsesJsonValues.ToBoundaryJson(packed.Result).Should().Be("""{"content":"x"}""");
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

    private static (ResponsesAgentToolStateCommandAdapter adapter, RecordingRuntime runtime, RecordingDispatchPort dispatch) CreateAdapter()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();
        var adapter = new ResponsesAgentToolStateCommandAdapter(runtime, dispatch);
        return (adapter, runtime, dispatch);
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
