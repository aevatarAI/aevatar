using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Foundation.Runtime.Persistence;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Core;

public sealed class ResponsesAgentToolStateGAgentTests
{
    [Fact]
    public async Task HandleApplyTodoWriteAsync_ShouldPersistAgentScopedTodoState()
    {
        var actor = CreateActor();
        await RegisterAsync(actor);

        var observedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:00:00+00:00"));
        const string argumentsJson = """{"todos":[{"id":"todo-1","content":"Ship","status":"pending"}]}""";
        var apply = new ApplyResponsesTodoWriteRequested
        {
            ScopeId = "scope-1",
            OwnerSubject = "owner-1",
            SourceResponseId = "resp_1",
            Arguments = ResponsesJsonValues.ParseBoundaryPayload(argumentsJson),
            ObservedAt = observedAt,
        };
        apply.TodoItems.AddRange(ResponsesTodoItemParser.Parse(argumentsJson, "resp_1", observedAt));
        await actor.HandleApplyTodoWriteAsync(apply);

        actor.State.TodoItems.Should().ContainSingle();
        actor.State.TodoItems[0].Id.Should().Be("todo-1");
        actor.State.TodoItems[0].SourceResponseId.Should().Be("resp_1");
        actor.State.LastAppliedEventVersion.Should().Be(2);
    }

    [Fact]
    public async Task HandleRecordWebTraceAsync_ShouldMaterializeCacheAndCountHits()
    {
        var actor = CreateActor();
        await RegisterAsync(actor);

        var resultPayload = ResponsesJsonValues.ParseBoundaryPayload("""{"content":"fresh"}""");
        await actor.HandleRecordWebTraceAsync(new RecordResponsesWebTraceRequested
        {
            SourceResponseId = "resp_1",
            TraceId = "trace-1",
            ToolName = "WebFetch",
            CacheKey = "cache-1",
            Url = "https://example.com",
            Result = resultPayload,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:00:00+00:00")),
        });
        await actor.HandleRecordWebTraceAsync(new RecordResponsesWebTraceRequested
        {
            SourceResponseId = "resp_2",
            TraceId = "trace-2",
            ToolName = "WebFetch",
            CacheKey = "cache-1",
            Url = "https://example.com",
            CacheHit = true,
            Result = resultPayload,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:01:00+00:00")),
        });

        actor.State.WebTraces.Should().HaveCount(2);
        ResponsesJsonValues.ToBoundaryJson(actor.State.WebTraces[0].Result)
            .Should().Be("""{"content":"fresh"}""");
        actor.State.WebCacheEntries.Should().ContainSingle();
        ResponsesJsonValues.ToBoundaryJson(actor.State.WebCacheEntries[0].Result)
            .Should().Be("""{"content":"fresh"}""");
        actor.State.WebCacheEntries[0].HitCount.Should().Be(1);
        actor.State.WebCacheEntries[0].LastHitAt.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleRecordTaskAsync_ShouldPersistTaskTopologyTrace()
    {
        var actor = CreateActor();
        await RegisterAsync(actor);

        await actor.HandleRecordTaskAsync(new RecordResponsesTaskRequested
        {
            SourceResponseId = "resp_1",
            TaskId = "task_1",
            ChildActorId = "responses-agent-tools-scope-task-1",
            Description = "summarize",
            Arguments = ResponsesJsonValues.ParseBoundaryPayload("""{"prompt":"summarize"}"""),
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"status":"accepted"}"""),
            Status = ResponsesAgentToolTaskStatus.Accepted,
        });

        actor.State.TaskTraces.Should().ContainSingle();
        actor.State.TaskTraces[0].ChildActorId.Should().Be("responses-agent-tools-scope-task-1");
        actor.State.TaskTraces[0].Status.Should().Be(ResponsesAgentToolTaskStatus.Accepted);
        ResponsesJsonValues.ToBoundaryJson(actor.State.TaskTraces[0].Arguments)
            .Should().Be("""{"prompt":"summarize"}""");
        ResponsesJsonValues.ToBoundaryJson(actor.State.TaskTraces[0].Result)
            .Should().Be("""{"status":"accepted"}""");
    }

    private static ResponsesAgentToolStateGAgent CreateActor() =>
        GAgentServiceTestKit.CreateStatefulAgent<ResponsesAgentToolStateGAgent, ResponsesAgentToolState>(
            new InMemoryEventStore(),
            "responses-agent-tools-test",
            static () => new ResponsesAgentToolStateGAgent());

    private static Task RegisterAsync(ResponsesAgentToolStateGAgent actor) =>
        actor.HandleRegisterAsync(new RegisterResponsesAgentToolStateRequested
        {
            Record = new ResponsesAgentToolStateRecord
            {
                ScopeId = "scope-1",
                OwnerSubject = "owner-1",
                CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:00:00+00:00")),
                UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:00:00+00:00")),
            },
        });
}
