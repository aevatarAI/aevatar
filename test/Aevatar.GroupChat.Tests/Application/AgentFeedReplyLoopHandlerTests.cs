using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Application.Workers;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class AgentFeedReplyLoopHandlerTests
{
    [Fact]
    public async Task HandleAsync_ShouldGenerateReplyAndAdvanceFeed()
    {
        var threadQueryPort = new StubGroupTimelineQueryPort
        {
            Thread = CreateThreadSnapshot(),
        };
        var feedCommandPort = new RecordingAgentFeedCommandPort();
        var threadCommandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort
        {
            Result = new ParticipantReplyGenerationResult("reply from feed"),
        };
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort();
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var handler = new AgentFeedReplyLoopHandler(
            feedCommandPort,
            threadQueryPort,
            threadCommandPort,
            replyPort,
            dispatchPort,
            projectionPort);

        await handler.HandleAsync(CreateHint());

        dispatchPort.Requests.Should().BeEmpty();
        replyPort.Requests.Should().ContainSingle();
        threadCommandPort.AppendCalls.Should().ContainSingle();
        threadCommandPort.AppendCalls[0].ParticipantAgentId.Should().Be("agent-alpha");
        threadCommandPort.AppendCalls[0].ReplyToMessageId.Should().Be("msg-user-1");
        threadCommandPort.AppendCalls[0].TopicId.Should().Be("topic-a");
        feedCommandPort.AdvanceCalls.Should().ContainSingle();
        feedCommandPort.AdvanceCalls[0].AgentId.Should().Be("agent-alpha");
        feedCommandPort.AdvanceCalls[0].SignalId.Should().Be("msg-user-1");
    }

    [Fact]
    public async Task HandleAsync_ShouldDispatchRuntimeAndAdvanceFeedWhenBindingExists()
    {
        var threadQueryPort = new StubGroupTimelineQueryPort
        {
            Thread = CreateThreadSnapshot(
                runtimeBindings:
                [
                    new GroupParticipantRuntimeBindingSnapshot(
                        "agent-alpha",
                        "tenant-a",
                        "app-a",
                        "demo",
                        "agent-alpha-service",
                        "chat",
                        "scope-1"),
                ]),
        };
        var feedCommandPort = new RecordingAgentFeedCommandPort();
        var threadCommandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort();
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort
        {
            Result = new ParticipantRuntimeDispatchResult(
                "runtime-root-1",
                "group-chat-reply|group-a|general|agent-alpha|evt-user-1|msg-user-1"),
        };
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var handler = new AgentFeedReplyLoopHandler(
            feedCommandPort,
            threadQueryPort,
            threadCommandPort,
            replyPort,
            dispatchPort,
            projectionPort);

        await handler.HandleAsync(CreateHint());

        dispatchPort.Requests.Should().ContainSingle();
        replyPort.Requests.Should().BeEmpty();
        threadCommandPort.AppendCalls.Should().BeEmpty();
        projectionPort.EnsureCalls.Should().ContainSingle();
        feedCommandPort.AdvanceCalls.Should().ContainSingle();
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

    private static GroupThreadSnapshot CreateThreadSnapshot(
        IReadOnlyList<GroupParticipantRuntimeBindingSnapshot>? runtimeBindings = null)
    {
        return new GroupThreadSnapshot(
            "group-chat:thread:group-a:general",
            "group-a",
            "general",
            "General",
            ["agent-alpha", "agent-beta"],
            runtimeBindings ?? [],
            [
                new GroupTimelineMessageSnapshot(
                    "msg-user-1",
                    1,
                    GroupMessageSenderKind.User,
                    "user-1",
                    "hello @agent-alpha",
                    string.Empty,
                    ["agent-alpha", "agent-beta"],
                    "topic-a",
                    GroupSignalKind.Question),
            ],
            2,
            "evt-user-1",
            DateTimeOffset.Parse("2026-03-25T00:00:00+00:00"));
    }
}
