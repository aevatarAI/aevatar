using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Projection.Contexts;
using Aevatar.GroupChat.Projection.Orchestration;
using Aevatar.GroupChat.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GroupChat.Tests.Projection;

public sealed class GroupParticipantReplySessionProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldPublishReplyCompletedEventForMatchingCommittedSession()
    {
        var publisher = new RecordingPublisher();
        var projector = new GroupParticipantReplySessionProjector(publisher);
        var context = new GroupParticipantReplyProjectionContext
        {
            RootActorId = "workflow-run-1",
            ProjectionKind = GroupChatProjectionKinds.ParticipantReply,
            SessionId = "group-chat-reply|group-a|general|topic-general|agent-alpha|evt-user-1|msg-user-1",
        };

        await projector.ProjectAsync(
            context,
            new EventEnvelope
            {
                Route = new EnvelopeRoute
                {
                    PublisherActorId = "workflow-run-1:role-alpha",
                },
                Payload = Any.Pack(new CommittedStateEventPublished
                {
                    StateEvent = new StateEvent
                    {
                        EventId = "evt-user-1",
                        Version = 2,
                        EventData = Any.Pack(new RoleChatSessionCompletedEvent
                        {
                            SessionId = context.SessionId,
                            Content = "reply from role",
                            Prompt = "hello @agent-alpha",
                        }),
                    },
                }),
            });

        publisher.Events.Should().ContainSingle();
        var observed = publisher.Events[0];
        observed.RootActorId.Should().Be("workflow-run-1");
        observed.SessionId.Should().Be(context.SessionId);
        observed!.GroupId.Should().Be("group-a");
        observed.ThreadId.Should().Be("general");
        observed.TopicId.Should().Be("topic-general");
        observed.ReplyToMessageId.Should().Be("msg-user-1");
        observed.ParticipantAgentId.Should().Be("agent-alpha");
        observed.ReplyMessageId.Should().Be(GroupParticipantReplyMessageIds.FromSource("agent-alpha", "evt-user-1"));
        observed.Content.Should().Be("reply from role");
    }

    private sealed class RecordingPublisher : Aevatar.GroupChat.Abstractions.Ports.IGroupParticipantReplyCompletedPublisher
    {
        public List<GroupParticipantReplyCompletedEvent> Events { get; } = [];

        public Task PublishAsync(GroupParticipantReplyCompletedEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }
}
