using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Application.Workers;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class GroupParticipantReplyCompletedServiceTests
{
    [Fact]
    public async Task SubscribeAsync_ShouldForwardCompletedEventToReplyRunCommandPort()
    {
        var streams = new InMemoryStreamProvider();
        var runCommandPort = new AwaitingParticipantReplyRunCommandPort();
        var service = new GroupParticipantReplyCompletedService(streams, runCommandPort);

        await using var subscription = await service.SubscribeAsync();
        await streams.GetStream(GroupParticipantReplyCompletedStreamIds.Global).ProduceAsync(
            new GroupParticipantReplyCompletedEvent
            {
                RootActorId = "runtime-root-1",
                SessionId = "group-chat-reply|group-a|general|topic-a|agent-alpha|evt-user-1|msg-user-1",
                GroupId = "group-a",
                ThreadId = "general",
                ReplyToMessageId = "msg-user-1",
                ParticipantAgentId = "agent-alpha",
                SourceEventId = "evt-user-1",
                ReplyMessageId = "participant-reply:agent-alpha:evt-user-1",
                Content = "reply from runtime",
                TopicId = "topic-a",
            });

        var complete = await runCommandPort.WaitForCompleteAsync();

        complete.RootActorId.Should().Be("runtime-root-1");
        complete.SessionId.Should().Be("group-chat-reply|group-a|general|topic-a|agent-alpha|evt-user-1|msg-user-1");
        complete.Content.Should().Be("reply from runtime");
        complete.ReplyMessageId.Should().Be("participant-reply:agent-alpha:evt-user-1");
    }

    private sealed class AwaitingParticipantReplyRunCommandPort : RecordingParticipantReplyRunCommandPort
    {
        private readonly TaskCompletionSource<CompleteParticipantReplyRunCommand> _completeTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<Aevatar.GroupChat.Abstractions.Commands.GroupCommandAcceptedReceipt> CompleteAsync(
            CompleteParticipantReplyRunCommand command,
            CancellationToken ct = default)
        {
            _completeTcs.TrySetResult(command);
            return base.CompleteAsync(command, ct);
        }

        public Task<CompleteParticipantReplyRunCommand> WaitForCompleteAsync() => _completeTcs.Task;
    }
}
