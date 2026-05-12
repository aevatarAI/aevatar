using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Contexts;
using Aevatar.GAgentService.Projection.Projectors;
using Aevatar.GAgentService.Projection.Queries;
using Aevatar.GAgentService.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class ResponseSessionCurrentStateProjectorTests
{
    private const string ActorId = "response-session-actor-1";

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCurrentState_AndQueryByResponseId()
    {
        var store = new RecordingDocumentStore<ResponseSessionCurrentStateReadModel>(x => x.Id);
        var projector = new ResponseSessionCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-04-27T00:00:00+00:00")));
        var reader = new ResponseSessionQueryReader(store);
        var observedAt = DateTimeOffset.Parse("2026-04-27T01:00:00+00:00");
        var record = BuildRecord("resp_1", previousResponseId: "resp_0", observedAt);

        await projector.ProjectAsync(
            new ResponseSessionCurrentStateProjectionContext
            {
                RootActorId = ActorId,
                ProjectionKind = "response-sessions",
            },
            WrapCommittedSessionState(record, stateVersion: 7, eventId: "evt-1", observedAt));

        var doc = await store.GetAsync(ResponseSessionIds.BuildKey("resp_1"));
        doc.Should().NotBeNull();
        doc!.ResponseId.Should().Be("resp_1");
        doc.PreviousResponseId.Should().Be("resp_0");
        doc.ScopeId.Should().Be("user-1");
        doc.OwnerSubject.Should().Be("user-1");
        doc.OriginKind.Should().Be((int)ResponseSessionOriginKind.ApiKey);
        doc.Status.Should().Be((int)ResponseSessionStatus.Completed);
        doc.ActorId.Should().Be(ActorId);
        doc.StateVersion.Should().Be(7);
        doc.ForwardedToolCalls.Should().ContainSingle();
        doc.ForwardedToolCalls[0].CallId.Should().Be("call_1");
        doc.ForwardedToolCalls[0].Status.Should().Be((int)ResponseSessionForwardedToolCallStatus.Received);

        var snapshot = await reader.GetByResponseIdAsync("resp_1");
        snapshot.Should().NotBeNull();
        snapshot!.PreviousResponseId.Should().Be("resp_0");
        snapshot.Status.Should().Be(ResponseSessionStatus.Completed);
        snapshot.ForwardedToolCalls.Should().ContainSingle();
        snapshot.ForwardedToolCalls![0].ResultJson.Should().Be("""{"temperature":28}""");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreState_WithMissingOwner()
    {
        var store = new RecordingDocumentStore<ResponseSessionCurrentStateReadModel>(x => x.Id);
        var projector = new ResponseSessionCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.UtcNow));
        var record = BuildRecord("resp_1", previousResponseId: null, DateTimeOffset.UtcNow);
        record.OwnerSubject = string.Empty;

        await projector.ProjectAsync(
            new ResponseSessionCurrentStateProjectionContext
            {
                RootActorId = ActorId,
                ProjectionKind = "response-sessions",
            },
            WrapCommittedSessionState(record, stateVersion: 1, eventId: "evt-bad", DateTimeOffset.UtcNow));

        (await store.ReadItemsAsync()).Should().BeEmpty();
    }

    private static ResponseSessionRecord BuildRecord(
        string responseId,
        string? previousResponseId,
        DateTimeOffset observedAt) =>
        new()
        {
            ResponseId = responseId,
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            OriginKind = ResponseSessionOriginKind.ApiKey,
            PreviousResponseId = previousResponseId ?? string.Empty,
            Status = ResponseSessionStatus.Completed,
            CreatedAt = Timestamp.FromDateTimeOffset(observedAt),
            UpdatedAt = Timestamp.FromDateTimeOffset(observedAt),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };

    private static EventEnvelope WrapCommittedSessionState(
        ResponseSessionRecord record,
        long stateVersion,
        string eventId,
        DateTimeOffset observedAt)
    {
        var state = new ResponseSessionState
        {
            Record = record.Clone(),
            LastAppliedEventVersion = stateVersion,
            LastEventId = eventId,
        };
        state.ForwardedToolCalls.Add(new ResponseSessionForwardedToolCall
        {
            CallId = "call_1",
            ToolName = "get_weather",
            SchemaHash = "schema-1",
            ArgumentsJson = """{"city":"Singapore"}""",
            Status = ResponseSessionForwardedToolCallStatus.Received,
            ResultJson = """{"temperature":28}""",
            EmittedAt = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(-2)),
            ReceivedAt = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(-1)),
            Expiry = Timestamp.FromDateTimeOffset(observedAt.AddHours(1)),
        });
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
                    EventData = Any.Pack(new ResponseSessionRegisteredEvent
                    {
                        Record = record.Clone(),
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }
}
