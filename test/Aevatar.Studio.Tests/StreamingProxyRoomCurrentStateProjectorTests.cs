using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.StreamingProxy;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class StreamingProxyRoomCurrentStateProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldUpsertRoomCurrentState_FromCommittedRoomState()
    {
        var dispatcher = new RecordingWriteDispatcher<StreamingProxyRoomCurrentStateDocument>();
        var projector = new StreamingProxyRoomCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-05-25T00:00:00Z")));
        var joinedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-05-25T10:00:00Z"));

        await projector.ProjectAsync(
            new StreamingProxyCurrentStateProjectionContext
            {
                RootActorId = "room-a",
                ProjectionKind = "streaming-proxy-current-state",
            },
            WrapCommitted(
                new GroupChatParticipantJoinedEvent
                {
                    AgentId = "agent-1",
                    DisplayName = "Alice",
                },
                new StreamingProxyGAgentState
                {
                    RoomName = "Room A",
                    Participants =
                    {
                        new StreamingProxyParticipant
                        {
                            AgentId = "agent-1",
                            DisplayName = "Alice",
                            JoinedAt = joinedAt,
                        },
                    },
                },
                version: 7,
                eventId: "evt-7"));

        dispatcher.Upserts.Should().ContainSingle();
        var document = dispatcher.Upserts[0];
        document.Id.Should().Be("room-a");
        document.ActorId.Should().Be("room-a");
        document.StateVersion.Should().Be(7);
        document.LastEventId.Should().Be("evt-7");
        document.StateRoot.Is(StreamingProxyGAgentState.Descriptor).Should().BeTrue();
        document.StateRoot.Unpack<StreamingProxyGAgentState>().Participants
            .Should().ContainSingle(p => p.AgentId == "agent-1" && p.DisplayName == "Alice");
    }

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        IMessage state,
        long version,
        string eventId)
    {
        return new EventEnvelope
        {
            Id = "env-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    Version = version,
                    EventId = eventId,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher<TReadModel> : IProjectionWriteDispatcher<TReadModel>
        where TReadModel : class, IProjectionReadModel
    {
        public List<TReadModel> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(TReadModel readModel, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionWriteResult.Applied());
        }
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
