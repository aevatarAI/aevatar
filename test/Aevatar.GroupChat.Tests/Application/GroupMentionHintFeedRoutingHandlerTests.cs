using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Application.Workers;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class GroupMentionHintFeedRoutingHandlerTests
{
    [Fact]
    public async Task HandleAsync_ShouldAcceptSignalWhenInterestEvaluatorReturnsDecision()
    {
        var feedCommandPort = new RecordingAgentFeedCommandPort();
        var interestEvaluator = new StubAgentFeedInterestEvaluator
        {
            Decision = new Aevatar.GroupChat.Abstractions.Feeds.AgentFeedInterestDecision(
                140,
                GroupFeedAcceptReason.DirectHint),
        };
        var handler = new GroupMentionHintFeedRoutingHandler(feedCommandPort, interestEvaluator);

        await handler.HandleAsync(new GroupMentionHint
        {
            GroupId = "group-a",
            ThreadId = "general",
            MessageId = "msg-1",
            ParticipantAgentId = "agent-alpha",
            SourceEventId = "evt-1",
            SourceStateVersion = 2,
            TimelineCursor = 1,
            DirectHintAgentIds =
            {
                "agent-alpha",
                "agent-beta",
            },
            TopicId = "topic-a",
            SenderKind = GroupMessageSenderKind.User,
            SenderId = "user-1",
            SignalKind = GroupSignalKind.Question,
        });

        feedCommandPort.AcceptCalls.Should().ContainSingle();
        var accepted = feedCommandPort.AcceptCalls[0];
        accepted.AgentId.Should().Be("agent-alpha");
        accepted.SignalId.Should().Be("msg-1");
        accepted.TopicId.Should().Be("topic-a");
        accepted.SenderId.Should().Be("user-1");
        accepted.SignalKind.Should().Be(GroupSignalKind.Question);
        accepted.AcceptReason.Should().Be(GroupFeedAcceptReason.DirectHint);
        accepted.RankScore.Should().Be(140);
    }

    [Fact]
    public async Task HandleAsync_ShouldIgnoreHintWhenInterestEvaluatorRejectsIt()
    {
        var feedCommandPort = new RecordingAgentFeedCommandPort();
        var interestEvaluator = new StubAgentFeedInterestEvaluator();
        var handler = new GroupMentionHintFeedRoutingHandler(feedCommandPort, interestEvaluator);

        await handler.HandleAsync(new GroupMentionHint
        {
            GroupId = "group-a",
            ThreadId = "general",
            MessageId = "msg-1",
            ParticipantAgentId = "agent-beta",
            SourceEventId = "evt-1",
            SourceStateVersion = 2,
            TimelineCursor = 1,
            DirectHintAgentIds =
            {
                "agent-alpha",
                "agent-beta",
            },
            TopicId = "topic-a",
            SenderKind = GroupMessageSenderKind.User,
            SenderId = "user-1",
            SignalKind = GroupSignalKind.Question,
        });

        feedCommandPort.AcceptCalls.Should().BeEmpty();
    }
}
