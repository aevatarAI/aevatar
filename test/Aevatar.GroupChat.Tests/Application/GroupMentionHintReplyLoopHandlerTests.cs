using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Application.Workers;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class GroupMentionHintReplyLoopHandlerTests
{
    [Fact]
    public async Task HandleAsync_ShouldGenerateReplyAndAppendBackToThread()
    {
        var queryPort = new StubGroupTimelineQueryPort
        {
            Thread = CreateThreadSnapshot(),
        };
        var commandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort
        {
            Result = new Aevatar.GroupChat.Abstractions.Participants.ParticipantReplyGenerationResult("reply from agent"),
        };
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort();
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var handler = new GroupMentionHintReplyLoopHandler(queryPort, commandPort, replyPort, dispatchPort, projectionPort);

        await handler.HandleAsync(CreateHint());

        dispatchPort.Requests.Should().BeEmpty();
        replyPort.Requests.Should().ContainSingle();
        replyPort.Requests[0].ParticipantAgentId.Should().Be("agent-alpha");
        commandPort.AppendCalls.Should().ContainSingle();
        commandPort.AppendCalls[0].ParticipantAgentId.Should().Be("agent-alpha");
        commandPort.AppendCalls[0].ReplyToMessageId.Should().Be("msg-user-1");
        commandPort.AppendCalls[0].MessageId.Should().Be("participant-reply:agent-alpha:evt-user-1");
        commandPort.AppendCalls[0].Text.Should().Be("reply from agent");
    }

    [Fact]
    public async Task HandleAsync_ShouldSkipWhenReplyAlreadyExists()
    {
        var queryPort = new StubGroupTimelineQueryPort
        {
            Thread = CreateThreadSnapshot(includeExistingReply: true),
        };
        var commandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort
        {
            Result = new Aevatar.GroupChat.Abstractions.Participants.ParticipantReplyGenerationResult("reply from agent"),
        };
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort();
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var handler = new GroupMentionHintReplyLoopHandler(queryPort, commandPort, replyPort, dispatchPort, projectionPort);

        await handler.HandleAsync(CreateHint());

        dispatchPort.Requests.Should().BeEmpty();
        replyPort.Requests.Should().BeEmpty();
        commandPort.AppendCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldSkipWhenTriggerMessageCannotBeResolved()
    {
        var queryPort = new StubGroupTimelineQueryPort
        {
            Thread = CreateThreadSnapshot(triggerMessageId: "other-message"),
        };
        var commandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort
        {
            Result = new Aevatar.GroupChat.Abstractions.Participants.ParticipantReplyGenerationResult("reply from agent"),
        };
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort();
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var handler = new GroupMentionHintReplyLoopHandler(queryPort, commandPort, replyPort, dispatchPort, projectionPort);

        await handler.HandleAsync(CreateHint());

        dispatchPort.Requests.Should().BeEmpty();
        replyPort.Requests.Should().BeEmpty();
        commandPort.AppendCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ShouldDispatchToParticipantRuntimeWhenBindingExists()
    {
        var queryPort = new StubGroupTimelineQueryPort
        {
            Thread = CreateThreadSnapshot(
                runtimeBindings:
                [
                    new GroupParticipantRuntimeBindingSnapshot(
                        "agent-alpha",
                        GroupParticipantRuntimeTargetKind.Service,
                        new GroupServiceRuntimeTargetSnapshot(
                            "tenant-a",
                            "app-a",
                            "demo",
                            "agent-alpha-service",
                            "chat",
                            "scope-1")),
                ]),
        };
        var commandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort
        {
            Result = new Aevatar.GroupChat.Abstractions.Participants.ParticipantReplyGenerationResult("reply from agent"),
        };
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort
        {
            Result = new Aevatar.GroupChat.Abstractions.Participants.ParticipantRuntimeDispatchResult(
                Aevatar.GroupChat.Abstractions.Participants.ParticipantRuntimeBackendKind.Service,
                Aevatar.GroupChat.Abstractions.Participants.ParticipantRuntimeCompletionMode.AsyncObserved,
                "runtime-root-1",
                "group-chat-reply|group-a|general|topic-general|agent-alpha|evt-user-1|msg-user-1",
                "participant-reply:agent-alpha:evt-user-1"),
        };
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var handler = new GroupMentionHintReplyLoopHandler(queryPort, commandPort, replyPort, dispatchPort, projectionPort);

        await handler.HandleAsync(CreateHint());

        dispatchPort.Requests.Should().ContainSingle();
        dispatchPort.Requests[0].Binding.ServiceTarget!.ServiceId.Should().Be("agent-alpha-service");
        projectionPort.EnsureCalls.Should().ContainSingle()
            .Which.Should().Be(("runtime-root-1", "group-chat-reply|group-a|general|topic-general|agent-alpha|evt-user-1|msg-user-1"));
        replyPort.Requests.Should().BeEmpty();
        commandPort.AppendCalls.Should().BeEmpty();
    }

    private static GroupMentionHint CreateHint() =>
        new()
        {
            GroupId = "group-a",
            ThreadId = "general",
            MessageId = "msg-user-1",
            ParticipantAgentId = "agent-alpha",
            SourceEventId = "evt-user-1",
            SourceStateVersion = 2,
            TimelineCursor = 1,
        };

    private static GroupThreadSnapshot CreateThreadSnapshot(
        bool includeExistingReply = false,
        string triggerMessageId = "msg-user-1",
        IReadOnlyList<GroupParticipantRuntimeBindingSnapshot>? runtimeBindings = null)
    {
        var messages = new List<GroupTimelineMessageSnapshot>
        {
            new(
                triggerMessageId,
                1,
                GroupMessageSenderKind.User,
                "user-1",
                "hello @agent-alpha",
                string.Empty,
                ["agent-alpha"]),
        };
        if (includeExistingReply)
        {
            messages.Add(new GroupTimelineMessageSnapshot(
                "participant-reply:agent-alpha:evt-user-1",
                2,
                GroupMessageSenderKind.Agent,
                "agent-alpha",
                "existing reply",
                "msg-user-1",
                []));
        }

        return new GroupThreadSnapshot(
            "group-chat:thread:group-a:general",
            "group-a",
            "general",
            "General",
            ["agent-alpha"],
            runtimeBindings ?? [],
            messages,
            3,
            "evt-2",
            DateTimeOffset.Parse("2026-03-25T00:00:00+00:00"));
    }
}
