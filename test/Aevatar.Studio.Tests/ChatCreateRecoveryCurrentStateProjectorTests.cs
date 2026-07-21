using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ChatCreateRecoveryCurrentStateProjectorTests
{
    private const string DeliveryActorId = "chat-history-delivery:actor-alpha";

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCreateRecoveryForIdempotentCreateDelivery()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ChatCreateRecoveryCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-21T02:00:00Z")));
        var state = new ChatTurnHistoryDeliveryState
        {
            DeliveryId = "delivery-business-alpha",
            ScopeId = "scope-alpha",
            ConversationId = "conversation-alpha",
            TurnId = "turn-alpha",
            UserText = "hello",
            WorkflowActorId = "workflow-actor-alpha",
            WorkflowCommandId = "workflow-command-alpha",
            Status = ChatTurnHistoryDeliveryStatus.AppendCommitted,
            CreateConversationIfMissing = true,
            CreateIdempotencyKey = "create-alpha",
            CreateRequestHash = "request-hash-alpha",
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ChatTurnHistoryDeliveryAppendResultRecordedEvent
                {
                    DeliveryActorId = DeliveryActorId,
                    ConversationId = "conversation-alpha",
                    TurnId = "turn-alpha",
                    Accepted = true,
                },
                state,
                version: 4,
                eventId: "evt-delivery-4",
                stateEventTimestamp: DateTimeOffset.Parse("2026-07-21T01:30:00Z")));

        var written = dispatcher.Upserts.Should().ContainSingle().Subject;
        written.Id.Should().Be(DeliveryActorId);
        written.ActorId.Should().Be(DeliveryActorId);
        written.DeliveryActorId.Should().Be(DeliveryActorId);
        written.StateVersion.Should().Be(4);
        written.SourceVersion.Should().Be(4);
        written.LastEventId.Should().Be("evt-delivery-4");
        written.ScopeId.Should().Be("scope-alpha");
        written.CreateIdempotencyKey.Should().Be("create-alpha");
        written.CreateRequestHash.Should().Be("request-hash-alpha");
        written.ConversationId.Should().Be("conversation-alpha");
        written.TurnId.Should().Be("turn-alpha");
        written.Status.Should().Be("append_committed");
        written.UpdatedAt.ToDateTimeOffset().Should().Be(DateTimeOffset.Parse("2026-07-21T01:30:00Z"));
    }

    [Fact]
    public async Task ProjectAsync_ShouldSkipDeliveryWithoutCreateIdempotencyKey()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ChatCreateRecoveryCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-21T02:00:00Z")));

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ChatTurnHistoryDeliveryReservedEvent
                {
                    DeliveryId = "delivery-business-alpha",
                    ScopeId = "scope-alpha",
                    ConversationId = "conversation-alpha",
                    TurnId = "turn-alpha",
                },
                new ChatTurnHistoryDeliveryState
                {
                    ScopeId = "scope-alpha",
                    ConversationId = "conversation-alpha",
                    TurnId = "turn-alpha",
                    Status = ChatTurnHistoryDeliveryStatus.Reserved,
                },
                version: 1,
                eventId: "evt-delivery-1",
                stateEventTimestamp: DateTimeOffset.Parse("2026-07-21T01:30:00Z")));

        dispatcher.Upserts.Should().BeEmpty();
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = DeliveryActorId,
        ProjectionKind = ChatTurnHistoryDeliveryGAgent.ProjectionKind,
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        ChatTurnHistoryDeliveryState state,
        long version,
        string eventId,
        DateTimeOffset stateEventTimestamp)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-21T01:00:00Z")),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(DeliveryActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTimeOffset(stateEventTimestamp),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<ChatCreateRecoveryCurrentStateDocument>
    {
        public List<ChatCreateRecoveryCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ChatCreateRecoveryCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
