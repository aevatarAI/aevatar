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

public sealed class ChatHistoryCreateRecoveryCurrentStateProjectorTests
{
    private const string RootActorId = "chat-history-delivery:actor";

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreSourceReservationThatDoesNotExposeWorkflowRecovery()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ChatHistoryCreateRecoveryCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-21T02:00:00Z")));
        var state = new ChatTurnHistoryDeliveryState
        {
            DeliveryId = "nyxid-delivery-a",
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            TurnId = "turn-a",
            SourceActorId = "nyxid-conversation-a",
            SourceCommandId = "command-a",
            CreateConversationIfMissing = true,
            ExposeCreateRecovery = false,
            Status = ChatTurnHistoryDeliveryStatus.Reserved,
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ChatTurnHistoryDeliveryReservedEvent
                {
                    DeliveryId = "nyxid-delivery-a",
                    SourceActorId = "nyxid-conversation-a",
                    SourceCommandId = "command-a",
                },
                state,
                version: 1,
                eventId: "evt-nyxid-delivery-1",
                stateEventTimestamp: DateTimeOffset.Parse("2026-07-21T01:30:00Z")));

        dispatcher.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCreateRecoveryFromDeliveryState()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ChatHistoryCreateRecoveryCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-21T02:00:00Z")));
        var state = new ChatTurnHistoryDeliveryState
        {
            DeliveryId = "chat-history-create-scope-a-create-command-1",
            ScopeId = "scope-a",
            ConversationId = "conversation-stable",
            TurnId = "turn-stable",
            UserText = "hello",
            SourceActorId = "run-stable",
            SourceCommandId = "create-command-1",
            SourceCorrelationId = "corr-1",
            RequestFingerprint = "fingerprint-1",
            Status = ChatTurnHistoryDeliveryStatus.AppendCommitted,
            ReservedAtUnixMs = 1784600000000,
            CompletedAtUnixMs = 1784600005000,
            CreateConversationIfMissing = true,
            ExposeCreateRecovery = true,
        };
        var stateEventTimestamp = DateTimeOffset.Parse("2026-07-21T01:30:00Z");

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ChatTurnHistoryDeliveryAppendResultRecordedEvent
                {
                    DeliveryActorId = RootActorId,
                    ConversationId = "conversation-stable",
                    TurnId = "turn-stable",
                    Accepted = true,
                },
                state,
                version: 4,
                eventId: "evt-delivery-4",
                stateEventTimestamp: stateEventTimestamp));

        var written = dispatcher.Upserts.Should().ContainSingle().Subject;
        written.Id.Should().Be(ChatHistoryCreateRecoveryIds.FromScopeAndCommandId("scope-a", "create-command-1"));
        written.ActorId.Should().Be(RootActorId);
        written.StateVersion.Should().Be(4);
        written.LastEventId.Should().Be("evt-delivery-4");
        written.UpdatedAt.ToDateTimeOffset().Should().Be(stateEventTimestamp);
        written.ScopeId.Should().Be("scope-a");
        written.ConversationId.Should().Be("conversation-stable");
        written.TurnId.Should().Be("turn-stable");
        written.WorkflowActorId.Should().Be("run-stable");
        written.WorkflowCommandId.Should().Be("create-command-1");
        written.WorkflowCorrelationId.Should().Be("corr-1");
        written.RequestFingerprint.Should().Be("fingerprint-1");
        written.Status.Should().Be("append_committed");
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = "studio-current-state",
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        ChatTurnHistoryDeliveryState state,
        long version,
        string eventId,
        DateTimeOffset stateEventTimestamp) =>
        new()
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-21T01:00:00Z")),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
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

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<ChatHistoryCreateRecoveryCurrentStateDocument>
    {
        public List<ChatHistoryCreateRecoveryCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ChatHistoryCreateRecoveryCurrentStateDocument readModel,
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
