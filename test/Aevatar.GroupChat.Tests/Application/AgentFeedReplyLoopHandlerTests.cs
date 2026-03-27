using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Application.Workers;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class AgentFeedReplyLoopHandlerTests
{
    [Fact]
    public async Task HandleAsync_ShouldStartParticipantReplyRun()
    {
        var runCommandPort = new RecordingParticipantReplyRunCommandPort();
        var handler = new AgentFeedReplyLoopHandler(runCommandPort);

        await handler.HandleAsync(CreateHint());

        runCommandPort.StartCalls.Should().ContainSingle();
        runCommandPort.StartCalls[0].GroupId.Should().Be("group-a");
        runCommandPort.StartCalls[0].ThreadId.Should().Be("general");
        runCommandPort.StartCalls[0].ParticipantAgentId.Should().Be("agent-alpha");
        runCommandPort.StartCalls[0].SignalId.Should().Be("msg-user-1");
        runCommandPort.StartCalls[0].SourceEventId.Should().Be("evt-user-1");
        runCommandPort.StartCalls[0].TopicId.Should().Be("topic-a");
    }

    private static AgentFeedHint CreateHint() =>
        new()
        {
            AgentId = "agent-alpha",
            SignalId = "msg-user-1",
            SourceEventId = "evt-user-1",
            GroupId = "group-a",
            ThreadId = "general",
            TopicId = "topic-a",
            SenderKind = GroupMessageSenderKind.User,
            SenderId = "user-1",
            SignalKind = GroupSignalKind.Question,
            SourceStateVersion = 2,
            TimelineCursor = 1,
            AcceptReason = GroupFeedAcceptReason.DirectHint,
            RankScore = 1000,
        };
}
