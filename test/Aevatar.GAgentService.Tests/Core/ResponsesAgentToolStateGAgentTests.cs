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
    public async Task HandleApplyTodoWriteAsync_ShouldIgnoreDuplicateWrite_AndKeepDurableStateStable()
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
        var versionAfterFirstWrite = actor.State.LastAppliedEventVersion;
        var updatedAtAfterFirstWrite = actor.State.Record!.UpdatedAt!.Clone();

        await actor.HandleApplyTodoWriteAsync(apply);

        actor.State.LastAppliedEventVersion.Should().Be(versionAfterFirstWrite);
        actor.State.Record!.UpdatedAt.Should().Be(updatedAtAfterFirstWrite);
        actor.State.TodoItems.Should().ContainSingle();
        actor.State.TodoItems[0].Id.Should().Be("todo-1");
        actor.State.TodoItems[0].SourceResponseId.Should().Be("resp_1");
    }

    [Fact]
    public async Task HandleRecordWebTraceAsync_ShouldMaterializeCacheAndCountHits()
    {
        var actor = CreateActor();
        await RegisterAsync(actor);

        var resultPayload = ResponsesWebResultMigration.FromFetch(new ResponsesWebFetchToolOutput
        {
            Url = "https://example.com",
            StatusCode = 200,
            Content = "fresh",
        });
        await actor.HandleRecordWebTraceAsync(new RecordResponsesWebTraceRequested
        {
            SourceResponseId = "resp_1",
            TraceId = "trace-1",
            ToolName = "WebFetch",
            CacheKey = "cache-1",
            Url = "https://example.com",
            TypedResult = resultPayload,
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
            TypedResult = resultPayload,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-12T00:01:00+00:00")),
        });

        actor.State.WebTraces.Should().HaveCount(2);
        actor.State.WebTraces[0].TypedResult.Fetch.Content.Should().Be("fresh");
        ResponsesJsonValues.ToBoundaryJson(actor.State.WebTraces[0].Result)
            .Should().Be("""{"url":"https://example.com","status_code":200,"content_type":"","content":"fresh"}""");
        actor.State.WebCacheEntries.Should().ContainSingle();
        actor.State.WebCacheEntries[0].TypedResult.Fetch.Content.Should().Be("fresh");
        ResponsesJsonValues.ToBoundaryJson(actor.State.WebCacheEntries[0].Result)
            .Should().Be("""{"url":"https://example.com","status_code":200,"content_type":"","content":"fresh"}""");
        actor.State.WebCacheEntries[0].HitCount.Should().Be(1);
        actor.State.WebCacheEntries[0].LastHitAt.Should().NotBeNull();
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
