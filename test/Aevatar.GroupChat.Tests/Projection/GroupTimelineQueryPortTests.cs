using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Projection.Queries;
using Aevatar.GroupChat.Projection.ReadModels;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Projection;

public sealed class GroupTimelineQueryPortTests
{
    [Fact]
    public async Task GetMentionedMessagesAsync_ShouldFilterByParticipantAndCursor()
    {
        var store = new RecordingDocumentStore<GroupTimelineReadModel>(x => x.Id);
        await store.UpsertAsync(new GroupTimelineReadModel
        {
            Id = "group-chat:thread:group-a:general",
            ActorId = "group-chat:thread:group-a:general",
            GroupId = "group-a",
            ThreadId = "general",
            DisplayName = "General",
            StateVersion = 4,
            LastEventId = "evt-4",
            ParticipantAgentIds =
            {
                "agent-alpha",
                "agent-beta",
            },
            Messages =
            {
                new GroupTimelineMessageReadModel
                {
                    MessageId = "msg-1",
                    TimelineCursor = 1,
                    SenderKindValue = (int)GroupMessageSenderKind.User,
                    SenderId = "user-1",
                    Text = "hello @agent-alpha",
                    TopicId = "topic-alpha",
                    SignalKindValue = (int)GroupSignalKind.Question,
                    DirectHintAgentIds =
                    {
                        "agent-alpha",
                    },
                },
                new GroupTimelineMessageReadModel
                {
                    MessageId = "msg-2",
                    TimelineCursor = 2,
                    SenderKindValue = (int)GroupMessageSenderKind.User,
                    SenderId = "user-2",
                    Text = "hello @agent-beta",
                    TopicId = "topic-beta",
                    DirectHintAgentIds =
                    {
                        "agent-beta",
                    },
                },
                new GroupTimelineMessageReadModel
                {
                    MessageId = "msg-3",
                    TimelineCursor = 3,
                    SenderKindValue = (int)GroupMessageSenderKind.User,
                    SenderId = "user-3",
                    Text = "follow-up @agent-alpha",
                    TopicId = "topic-alpha",
                    DirectHintAgentIds =
                    {
                        "agent-alpha",
                    },
                },
            },
        });
        var queryPort = new GroupTimelineQueryPort(store);

        var messages = await queryPort.GetMentionedMessagesAsync("group-a", "general", "agent-alpha", sinceCursor: 1);

        messages.Should().HaveCount(1);
        messages[0].MessageId.Should().Be("msg-3");
        messages[0].TimelineCursor.Should().Be(3);
        messages[0].SenderKind.Should().Be(GroupMessageSenderKind.User);
        messages[0].DirectHintAgentIds.Should().ContainSingle().Which.Should().Be("agent-alpha");
        messages[0].TopicId.Should().Be("topic-alpha");
    }

    [Fact]
    public async Task GetThreadAsync_ShouldMapDocumentToSnapshot()
    {
        var store = new RecordingDocumentStore<GroupTimelineReadModel>(x => x.Id);
        await store.UpsertAsync(new GroupTimelineReadModel
        {
            Id = "group-chat:thread:group-a:general",
            ActorId = "group-chat:thread:group-a:general",
            GroupId = "group-a",
            ThreadId = "general",
            DisplayName = "General",
            StateVersion = 5,
            LastEventId = "evt-5",
            ParticipantAgentIds =
            {
                "agent-alpha",
            },
            Messages =
            {
                new GroupTimelineMessageReadModel
                {
                    MessageId = "msg-1",
                    TimelineCursor = 1,
                    SenderKindValue = (int)GroupMessageSenderKind.Agent,
                    SenderId = "agent-alpha",
                    Text = "done",
                    ReplyToMessageId = "msg-user-1",
                    TopicId = "topic-general",
                    SignalKindValue = (int)GroupSignalKind.Result,
                },
            },
        });
        var queryPort = new GroupTimelineQueryPort(store);

        var snapshot = await queryPort.GetThreadAsync("group-a", "general");

        snapshot.Should().NotBeNull();
        snapshot!.ActorId.Should().Be("group-chat:thread:group-a:general");
        snapshot.Messages.Should().ContainSingle();
        snapshot.Messages[0].SenderKind.Should().Be(GroupMessageSenderKind.Agent);
        snapshot.Messages[0].ReplyToMessageId.Should().Be("msg-user-1");
        snapshot.Messages[0].TopicId.Should().Be("topic-general");
        snapshot.Messages[0].SignalKind.Should().Be(GroupSignalKind.Result);
    }
}
