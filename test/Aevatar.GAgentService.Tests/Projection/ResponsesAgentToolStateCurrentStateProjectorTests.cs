using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using ProtoValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ResponsesAgentToolStateCurrentStateProjectorTests
{
    private const string ScopeId = "scope-1";
    private const string OwnerSubject = "owner-1";
    private static readonly string ActorId = ResponseAgentToolStateIds.BuildActorId(ScopeId, OwnerSubject);

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeTodoWebState_AndQueryCache()
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
        doc.WebTraces.Should().ContainSingle(x => x.TraceId == "trace-1");
        doc.WebCacheEntries.Should().ContainSingle(x => x.CacheKey == "cache-1");

        var snapshot = await reader.GetAsync(ScopeId, OwnerSubject);
        snapshot.Should().NotBeNull();
        snapshot!.Todos.Should().ContainSingle(x => x.Content == "Ship");
        snapshot.WebTraces.Should().ContainSingle(x => x.TraceId == "trace-1");
        snapshot.WebTraces[0].Result.Fetch.Content.Should().Be("fresh");

        var cache = await reader.GetWebCacheEntryAsync(ScopeId, OwnerSubject, "WebFetch", "cache-1");
        cache.Should().NotBeNull();
        cache!.Result.Fetch.Content.Should().Be("fresh");
    }

    [Fact]
    public async Task QueryReader_ShouldRemapFreshSnapshotFromPersistedReadModelAcrossRepeatedReads()
    {
        var store = new RecordingDocumentStore<ResponsesAgentToolStateCurrentStateReadModel>(x => x.Id);
        var projector = new ResponsesAgentToolStateCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-12T00:00:00+00:00")));
        var observedAt = DateTimeOffset.Parse("2026-05-12T00:01:00+00:00");

        await projector.ProjectAsync(
            new ResponsesAgentToolStateCurrentStateProjectionContext
            {
                RootActorId = ActorId,
                ProjectionKind = "responses-agent-tools",
            },
            WrapCommittedState(observedAt));

        var reader = new ResponsesAgentToolStateQueryReader(store);
        var first = await reader.GetAsync(ScopeId, OwnerSubject);
        var persisted = await store.GetAsync(ActorId);

        persisted.Should().NotBeNull();
        persisted!.Todos[0].Content = "Store changed";
        persisted.WebCacheEntries[0].HitCount = 9;

        var second = await new ResponsesAgentToolStateQueryReader(store).GetAsync(ScopeId, OwnerSubject);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Todos.Should().ContainSingle(x => x.Content == "Ship");
        first.WebCacheEntries.Should().ContainSingle(x => x.HitCount == 0);
        second.Should().NotBeSameAs(first);
        second!.ActorId.Should().Be(ActorId);
        second.StateVersion.Should().Be(4);
        second.Todos.Should().ContainSingle(x => x.Id == "todo-1" && x.Content == "Store changed");
        second.WebCacheEntries.Should().ContainSingle(x => x.CacheKey == "cache-1" && x.HitCount == 9);
    }

    [Fact]
    public async Task QueryReader_ShouldUseLegacyValueFallback_WhenReadModelHasNoTypedResult()
    {
        var store = new RecordingDocumentStore<ResponsesAgentToolStateCurrentStateReadModel>(x => x.Id);
        var observedAt = DateTimeOffset.Parse("2026-05-12T00:01:00+00:00");
        var legacyResult = new ProtoValue { StructValue = new Struct() };
        legacyResult.StructValue.Fields["url"] = ProtoValue.ForString("https://legacy.example.com");
        legacyResult.StructValue.Fields["status_code"] = ProtoValue.ForNumber(202);
        legacyResult.StructValue.Fields["content"] = ProtoValue.ForString("legacy");

        await store.UpsertAsync(new ResponsesAgentToolStateCurrentStateReadModel
        {
            Id = ActorId,
            ActorId = ActorId,
            ScopeId = ScopeId,
            OwnerSubject = OwnerSubject,
            StateVersion = 3,
            CreatedAt = observedAt.AddMinutes(-1),
            UpdatedAt = observedAt,
            WebTraces =
            {
                new ResponsesWebTraceReadModel
                {
                    TraceId = "trace-legacy",
                    SourceResponseId = "resp_legacy",
                    ToolName = "WebFetch",
                    CacheKey = "cache-legacy",
                    Url = "https://legacy.example.com",
                    Result = legacyResult.Clone(),
                    ObservedAt = observedAt,
                },
            },
            WebCacheEntries =
            {
                new ResponsesWebCacheEntryReadModel
                {
                    CacheKey = "cache-legacy",
                    ToolName = "WebFetch",
                    Url = "https://legacy.example.com",
                    Result = legacyResult.Clone(),
                    CachedAt = observedAt,
                    HitCount = 2,
                },
            },
        });

        var reader = new ResponsesAgentToolStateQueryReader(store);

        var snapshot = await reader.GetAsync(ScopeId, OwnerSubject);
        var cache = await reader.GetWebCacheEntryAsync(ScopeId, OwnerSubject, "WebFetch", "cache-legacy");

        snapshot.Should().NotBeNull();
        snapshot!.WebTraces.Should().ContainSingle();
        snapshot.WebTraces[0].Result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Fetch);
        snapshot.WebTraces[0].Result.Fetch.Content.Should().Be("legacy");
        cache.Should().NotBeNull();
        cache!.Result.ResultCase.Should().Be(ResponsesWebToolResult.ResultOneofCase.Fetch);
        cache.Result.Fetch.StatusCode.Should().Be(202);
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
        state.WebTraces.Add(new ResponsesWebTrace
        {
            TraceId = "trace-1",
            SourceResponseId = "resp_1",
            ToolName = "WebFetch",
            CacheKey = "cache-1",
            Url = "https://example.com",
            TypedResult = ResponsesWebResultMigration.FromFetch(new ResponsesWebFetchToolOutput
            {
                Url = "https://example.com",
                StatusCode = 200,
                Content = "fresh",
            }),
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
        });
        state.WebCacheEntries.Add(new ResponsesWebCacheEntry
        {
            CacheKey = "cache-1",
            ToolName = "WebFetch",
            Url = "https://example.com",
            TypedResult = ResponsesWebResultMigration.FromFetch(new ResponsesWebFetchToolOutput
            {
                Url = "https://example.com",
                StatusCode = 200,
                Content = "fresh",
            }),
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
