using Aevatar.AI.Abstractions.LLMProviders;
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

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class LlmSessionCurrentStateProjectorTests
{
    private const string ActorId = "response-session-actor-1";

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCurrentState_AndQueryByResponseId()
    {
        var store = new RecordingDocumentStore<LlmSessionCurrentStateReadModel>(x => x.Id);
        var projector = new LlmSessionCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-04-27T00:00:00+00:00")));
        var reader = new LlmSessionQueryReader(store);
        var observedAt = DateTimeOffset.Parse("2026-04-27T01:00:00+00:00");
        var record = BuildRecord("resp_1", previousResponseId: "resp_0", observedAt);

        await projector.ProjectAsync(
            new LlmSessionCurrentStateProjectionContext
            {
                RootActorId = ActorId,
                ProjectionKind = "response-sessions",
            },
            WrapCommittedSessionState(record, stateVersion: 7, eventId: "evt-1", observedAt));

        var doc = await store.GetAsync(LlmSessionIds.BuildKey("resp_1"));
        doc.Should().NotBeNull();
        doc!.ResponseId.Should().Be("resp_1");
        doc.PreviousResponseId.Should().Be("resp_0");
        doc.ScopeId.Should().Be("user-1");
        doc.OwnerSubject.Should().Be("user-1");
        doc.OriginKind.Should().Be((int)LlmSessionOriginKind.ApiKey);
        doc.Status.Should().Be((int)LlmSessionStatus.Completed);
        doc.ActorId.Should().Be(ActorId);
        doc.StateVersion.Should().Be(7);
        doc.ForwardedToolCalls.Should().ContainSingle();
        doc.ForwardedToolCalls[0].CallId.Should().Be("call_1");
        doc.ForwardedToolCalls[0].Status.Should().Be((int)LlmSessionForwardedToolCallStatus.Received);
        doc.Completion.Should().NotBeNull();
        doc.Completion!.OutputText.Should().Be("completed text");
        doc.Completion.ToolCalls.Should().ContainSingle();
        doc.Completion.ToolCalls[0].CallId.Should().Be("call_done");
        ResponsesJsonValues.ToBoundaryJson(doc.Completion.ToolCalls[0].Result)
            .Should().Be("""{"result":true}""");
        doc.Completion.Usage.Should().NotBeNull();
        doc.Completion.Usage!.PromptTokens.Should().Be(10);
        doc.Completion.Usage.CompletionTokens.Should().Be(11);
        doc.Completion.Usage.TotalTokens.Should().Be(21);
        doc.Completion.FailureCode.Should().BeEmpty();

        var snapshot = await reader.GetByResponseIdAsync("resp_1");
        snapshot.Should().NotBeNull();
        snapshot!.PreviousResponseId.Should().Be("resp_0");
        snapshot.Status.Should().Be(LlmSessionStatus.Completed);
        snapshot.ForwardedToolCalls.Should().ContainSingle();
        snapshot.ForwardedToolCalls![0].ResultJson.Should().Be("""{"temperature":28}""");
        snapshot.Completion.Should().NotBeNull();
        snapshot.Completion!.OutputText.Should().Be("completed text");
        snapshot.Completion.Usage.Should().Be(new TokenUsage(10, 11, 21));
        snapshot.Completion.ToolCalls.Should().ContainSingle()
            .Which.ResultJson.Should().Be("""{"result":true}""");
    }

    [Fact]
    public async Task QueryReader_ShouldMapCompletionToolResultJson_AndFailureFields()
    {
        var store = new RecordingDocumentStore<LlmSessionCurrentStateReadModel>(x => x.Id);
        var reader = new LlmSessionQueryReader(store);
        await store.UpsertAsync(new LlmSessionCurrentStateReadModel
        {
            Id = LlmSessionIds.BuildKey("resp_failed"),
            ResponseId = "resp_failed",
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            OriginKind = (int)LlmSessionOriginKind.ApiKey,
            Status = (int)LlmSessionStatus.Failed,
            ActorId = ActorId,
            StateVersion = 4,
            LastEventId = "evt-failed",
            CreatedAt = DateTimeOffset.Parse("2026-04-27T01:00:00+00:00"),
            TtlSeconds = (long)TimeSpan.FromHours(1).TotalSeconds,
            Completion = new LlmSessionCompletionReadModel
            {
                OutputText = "partial",
                CompletedAt = DateTimeOffset.Parse("2026-04-27T01:01:00+00:00"),
                FailureCode = "gagent_invocation_failed",
                FailureMessage = "GAgent invocation failed.",
                ToolCalls =
                {
                    new LlmSessionCompletedToolCallReadModel
                    {
                        CallId = "call_failed",
                        ToolName = "WebFetch",
                        Result = ResponsesJsonValues.ParseBoundaryPayload("""{"error":"boom"}"""),
                    },
                },
            },
        });

        var snapshot = await reader.GetByResponseIdAsync("resp_failed");

        snapshot.Should().NotBeNull();
        snapshot!.Completion.Should().NotBeNull();
        snapshot.Completion!.OutputText.Should().Be("partial");
        snapshot.Completion.FailureCode.Should().Be("gagent_invocation_failed");
        snapshot.Completion.FailureMessage.Should().Be("GAgent invocation failed.");
        snapshot.Completion.ToolCalls.Should().ContainSingle()
            .Which.ResultJson.Should().Be("""{"error":"boom"}""");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreState_WithMissingOwner()
    {
        var store = new RecordingDocumentStore<LlmSessionCurrentStateReadModel>(x => x.Id);
        var projector = new LlmSessionCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.UtcNow));
        var record = BuildRecord("resp_1", previousResponseId: null, DateTimeOffset.UtcNow);
        record.OwnerSubject = string.Empty;

        await projector.ProjectAsync(
            new LlmSessionCurrentStateProjectionContext
            {
                RootActorId = ActorId,
                ProjectionKind = "response-sessions",
            },
            WrapCommittedSessionState(record, stateVersion: 1, eventId: "evt-bad", DateTimeOffset.UtcNow));

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task QueryReader_ShouldSynthesizeExpiredToolCallResult_WhenReadModelHasNoResult()
    {
        var store = new RecordingDocumentStore<LlmSessionCurrentStateReadModel>(x => x.Id);
        var reader = new LlmSessionQueryReader(store);
        await store.UpsertAsync(new LlmSessionCurrentStateReadModel
        {
            Id = LlmSessionIds.BuildKey("resp_1"),
            ResponseId = "resp_1",
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            OriginKind = (int)LlmSessionOriginKind.ApiKey,
            Status = (int)LlmSessionStatus.Expired,
            ActorId = ActorId,
            StateVersion = 3,
            CreatedAt = DateTimeOffset.Parse("2026-04-27T01:00:00+00:00"),
            TtlSeconds = (long)TimeSpan.FromHours(1).TotalSeconds,
            ForwardedToolCalls =
            [
                new LlmSessionForwardedToolCallReadModel
                {
                    CallId = "call_1",
                    ToolName = "get_weather",
                    SchemaHash = "schema-1",
                    Arguments = ResponsesJsonValues.ParseBoundaryPayload("""{"city":"Singapore"}"""),
                    Status = (int)LlmSessionForwardedToolCallStatus.Expired,
                },
            ],
        });

        var snapshot = await reader.GetByResponseIdAsync("resp_1");

        snapshot.Should().NotBeNull();
        snapshot!.ForwardedToolCalls.Should().ContainSingle();
        snapshot.ForwardedToolCalls![0].ResultJson
            .Should().Be("""{"error":"tool_call_expired","call_id":"call_1"}""");
    }

    private static LlmSessionRecord BuildRecord(
        string responseId,
        string? previousResponseId,
        DateTimeOffset observedAt) =>
        new()
        {
            ResponseId = responseId,
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            OriginKind = LlmSessionOriginKind.ApiKey,
            PreviousResponseId = previousResponseId ?? string.Empty,
            Status = LlmSessionStatus.Completed,
            CreatedAt = Timestamp.FromDateTimeOffset(observedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };

    private static EventEnvelope WrapCommittedSessionState(
        LlmSessionRecord record,
        long stateVersion,
        string eventId,
        DateTimeOffset observedAt)
    {
        var state = new LlmSessionState
        {
            Record = record.Clone(),
            LastAppliedEventVersion = stateVersion,
            LastEventId = eventId,
        };
        state.ForwardedToolCalls.Add(new LlmSessionForwardedToolCall
        {
            CallId = "call_1",
            ToolName = "get_weather",
            SchemaHash = "schema-1",
            Arguments = ResponsesJsonValues.ParseBoundaryPayload("""{"city":"Singapore"}"""),
            Status = LlmSessionForwardedToolCallStatus.Received,
            Result = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
            EmittedAt = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(-2)),
            ReceivedAt = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(-1)),
            Expiry = Timestamp.FromDateTimeOffset(observedAt.AddHours(1)),
        });
        state.Completion = new LlmSessionCompletion
        {
            OutputText = "completed text",
            CompletedAt = Timestamp.FromDateTimeOffset(observedAt),
            Usage = new LlmSessionTokenUsage
            {
                PromptTokens = 10,
                CompletionTokens = 11,
                TotalTokens = 21,
            },
            ToolCalls =
            {
                new LlmSessionCompletedToolCall
                {
                    CallId = "call_done",
                    ToolName = "get_weather",
                    Result = ResponsesJsonValues.ParseBoundaryPayload("""{"result":true}"""),
                },
            },
        };
        return new EventEnvelope
        {
            Id = $"outer-{eventId}",
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("root-actor"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = stateVersion,
                    Timestamp = Timestamp.FromDateTimeOffset(observedAt),
                    EventData = Any.Pack(new LlmSessionRegisteredEvent
                    {
                        Record = record.Clone(),
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }
}
