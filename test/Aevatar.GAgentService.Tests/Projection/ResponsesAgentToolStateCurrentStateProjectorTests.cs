using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ResponsesAgentToolStateCurrentStateProjectorTests
{
    private const string ScopeId = "scope-1";
    private const string OwnerSubject = "owner-1";
    private static readonly string ActorId = ResponseAgentToolStateIds.BuildActorId(ScopeId, OwnerSubject);

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeTodoTaskWebState_AndQueryCache()
    {
        var store = new RecordingDocumentStore<ResponsesAgentToolStateCurrentStateReadModel>(x => x.Id);
        var projector = new ResponsesAgentToolStateCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-12T00:00:00+00:00")));
        var reader = new ResponsesAgentToolStateQueryReader(store);
        var observedAt = DateTimeOffset.Parse("2026-05-12T00:01:00+00:00");

        await projector.ProjectAsync(
            new ResponsesAgentToolStateCurrentStateProjectionContext
            {
                RootActorId = ActorId,
                ProjectionKind = "responses-agent-tools",
            },
            WrapCommittedState(observedAt));

        var doc = await store.GetAsync(ActorId);
        doc.Should().NotBeNull();
        doc!.Todos.Should().ContainSingle(x => x.Id == "todo-1");
        doc.Tasks.Should().ContainSingle(x => x.TaskId == "task_1");
        doc.WebTraces.Should().ContainSingle(x => x.TraceId == "trace-1");
        doc.WebCacheEntries.Should().ContainSingle(x => x.CacheKey == "cache-1");

        var snapshot = await reader.GetAsync(ScopeId, OwnerSubject);
        snapshot.Should().NotBeNull();
        snapshot!.Todos.Should().ContainSingle(x => x.Content == "Ship");
        snapshot.Tasks.Should().ContainSingle(x => x.ChildActorId == "child-1");

        var cache = await reader.GetWebCacheEntryAsync(ScopeId, OwnerSubject, "WebFetch", "cache-1");
        cache.Should().NotBeNull();
        cache!.ResultJson.Should().Be("""{"content":"fresh"}""");
    }

    private static EventEnvelope WrapCommittedState(DateTimeOffset observedAt)
    {
        var state = new ResponsesAgentToolState
        {
            Record = new ResponsesAgentToolStateRecord
            {
                ScopeId = ScopeId,
                OwnerSubject = OwnerSubject,
                CreatedAt = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(-1)),
                UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
            },
            LastAppliedEventVersion = 4,
            LastEventId = "evt-4",
        };
        state.TodoItems.Add(new ResponsesTodoItem
        {
            Id = "todo-1",
            Content = "Ship",
            Status = "pending",
            SourceResponseId = "resp_1",
            CreatedAt = Timestamp.FromDateTimeOffset(observedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
        });
        state.TaskTraces.Add(new ResponsesTaskTrace
        {
            TaskId = "task_1",
            SourceResponseId = "resp_1",
            ChildActorId = "child-1",
            Description = "summarize",
            Status = ResponsesAgentToolTaskStatus.Accepted,
            ArgumentsJson = "{}",
            ResultJson = """{"status":"accepted"}""",
            CreatedAt = Timestamp.FromDateTimeOffset(observedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
        });
        state.WebTraces.Add(new ResponsesWebTrace
        {
            TraceId = "trace-1",
            SourceResponseId = "resp_1",
            ToolName = "WebFetch",
            CacheKey = "cache-1",
            Url = "https://example.com",
            ResultJson = """{"content":"fresh"}""",
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
        });
        state.WebCacheEntries.Add(new ResponsesWebCacheEntry
        {
            CacheKey = "cache-1",
            ToolName = "WebFetch",
            Url = "https://example.com",
            ResultJson = """{"content":"fresh"}""",
            CachedAt = Timestamp.FromDateTimeOffset(observedAt),
        });

        return new EventEnvelope
        {
            Id = "outer-evt-4",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("root-actor"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-4",
                    Version = 4,
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                    EventData = Any.Pack(new ResponsesWebTraceRecordedEvent
                    {
                        Trace = state.WebTraces[0].Clone(),
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }
}
