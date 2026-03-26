using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Projection.Contexts;
using Aevatar.GroupChat.Projection.Projectors;
using Aevatar.GroupChat.Projection.ReadModels;
using Aevatar.GroupChat.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GroupChat.Tests.Projection;

public sealed class GroupTimelineCurrentStateProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldMaterializeCurrentStateReplica()
    {
        var store = new RecordingDocumentStore<GroupTimelineReadModel>(x => x.Id);
        var projector = new GroupTimelineCurrentStateProjector(
            store,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-03-25T00:00:00+00:00")));
        var context = new GroupTimelineProjectionContext
        {
            RootActorId = "group-chat:thread:group-a:general",
            ProjectionKind = "group-chat-timeline",
        };
        var state = new GroupThreadState
        {
            GroupId = "group-a",
            ThreadId = "general",
            DisplayName = "General",
            ParticipantAgentIds =
            {
                "agent-alpha",
                "agent-beta",
            },
            MessageEntries =
            {
                new GroupThreadMessageState
                {
                    MessageId = "msg-user-1",
                    TimelineCursor = 1,
                    SenderKind = GroupMessageSenderKind.User,
                    SenderId = "user-1",
                    Text = "hello @agent-alpha",
                    TopicId = "topic-general",
                    SignalKind = GroupSignalKind.Question,
                    DirectHintAgentIds =
                    {
                        "agent-alpha",
                    },
                },
            },
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Id = "outer-1",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-25T09:00:00+00:00")),
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-1",
                        Version = 2,
                        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-03-25T09:00:00+00:00")),
                    },
                    StateRoot = Any.Pack(state),
                }),
            });

        var readModel = await store.GetAsync("group-chat:thread:group-a:general");
        readModel.Should().NotBeNull();
        readModel!.GroupId.Should().Be("group-a");
        readModel.ThreadId.Should().Be("general");
        readModel.StateVersion.Should().Be(2);
        readModel.LastEventId.Should().Be("evt-1");
        readModel.Messages.Should().ContainSingle();
        readModel.Messages[0].DirectHintAgentIds.Should().ContainSingle().Which.Should().Be("agent-alpha");
        readModel.Messages[0].TopicId.Should().Be("topic-general");
        readModel.Messages[0].SignalKindValue.Should().Be((int)GroupSignalKind.Question);
        readModel.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-03-25T09:00:00+00:00"));
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreEnvelopeWithoutCommittedState()
    {
        var store = new RecordingDocumentStore<GroupTimelineReadModel>(x => x.Id);
        var projector = new GroupTimelineCurrentStateProjector(store, new FixedProjectionClock(DateTimeOffset.UtcNow));

        await projector.ProjectAsync(
            new GroupTimelineProjectionContext
            {
                RootActorId = "group-chat:thread:group-a:general",
                ProjectionKind = "group-chat-timeline",
            },
            new EventEnvelope
            {
                Id = "outer-noop",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            });

        (await store.QueryAsync(new ProjectionDocumentQuery { Take = 10 })).Items.Should().BeEmpty();
    }
}
