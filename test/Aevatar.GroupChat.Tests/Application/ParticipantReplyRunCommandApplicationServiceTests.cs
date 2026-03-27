using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Application.Services;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class ParticipantReplyRunCommandApplicationServiceTests
{
    [Fact]
    public async Task StartAsync_ShouldEnsureRunActorAndDispatchEnvelope()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = new ParticipantReplyRunCommandApplicationService(runtime, dispatchPort);

        var receipt = await service.StartAsync(
            new StartParticipantReplyRunCommand
            {
                GroupId = "group-a",
                ThreadId = "general",
                ParticipantAgentId = "agent-alpha",
                SignalId = "msg-user-1",
                SourceEventId = "evt-user-1",
                SourceStateVersion = 2,
                TimelineCursor = 1,
                TopicId = "topic-a",
            });

        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].actorId.Should().Be("group-chat:reply-run:group-a:general:agent-alpha:evt-user-1");
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be("group-chat:reply-run:group-a:general:agent-alpha:evt-user-1");
        dispatchPort.Calls[0].envelope.Payload!.Is(StartParticipantReplyRunCommand.Descriptor).Should().BeTrue();
        receipt.TargetActorId.Should().Be("group-chat:reply-run:group-a:general:agent-alpha:evt-user-1");
    }

    [Fact]
    public async Task CompleteAsync_ShouldDispatchToDeterministicRunActor()
    {
        var runtime = new RecordingActorRuntime();
        runtime.ExistingActorIds.Add("group-chat:reply-run:group-a:general:agent-alpha:evt-user-1");
        var dispatchPort = new RecordingActorDispatchPort();
        var service = new ParticipantReplyRunCommandApplicationService(runtime, dispatchPort);

        var receipt = await service.CompleteAsync(
            new CompleteParticipantReplyRunCommand
            {
                RootActorId = "runtime-root-1",
                SessionId = "group-chat-reply|group-a|general|topic-a|agent-alpha|evt-user-1|msg-user-1",
                GroupId = "group-a",
                ThreadId = "general",
                ReplyToMessageId = "msg-user-1",
                ParticipantAgentId = "agent-alpha",
                SourceEventId = "evt-user-1",
                ReplyMessageId = "participant-reply:agent-alpha:evt-user-1",
                Content = "reply",
                TopicId = "topic-a",
            });

        runtime.CreateCalls.Should().BeEmpty();
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].envelope.Payload!.Is(CompleteParticipantReplyRunCommand.Descriptor).Should().BeTrue();
        receipt.TargetActorId.Should().Be("group-chat:reply-run:group-a:general:agent-alpha:evt-user-1");
    }
}
