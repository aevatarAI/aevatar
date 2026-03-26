using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Application.Workers;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class GroupParticipantReplyCompletedServiceTests
{
    [Fact]
    public async Task SubscribeAsync_ShouldAppendReplyAndReleaseProjectionAfterCompletedEvent()
    {
        var streams = new InMemoryStreamProvider();
        var commandPort = new AwaitingGroupThreadCommandPort();
        var projectionPort = new AwaitingGroupParticipantReplyProjectionPort();
        var service = new GroupParticipantReplyCompletedService(streams, commandPort, projectionPort);

        await using var subscription = await service.SubscribeAsync();
        await streams.GetStream(GroupParticipantReplyCompletedStreamIds.Global).ProduceAsync(
            new GroupParticipantReplyCompletedEvent
            {
                RootActorId = "runtime-root-1",
                SessionId = "group-chat-reply|group-a|general|agent-alpha|evt-user-1|msg-user-1",
                GroupId = "group-a",
                ThreadId = "general",
                ReplyToMessageId = "msg-user-1",
                ParticipantAgentId = "agent-alpha",
                SourceEventId = "evt-user-1",
                ReplyMessageId = "participant-reply:agent-alpha:evt-user-1",
                Content = "reply from runtime",
            });

        var append = await commandPort.WaitForAppendAsync();
        var released = await projectionPort.WaitForReleaseAsync();

        append.Text.Should().Be("reply from runtime");
        append.ParticipantAgentId.Should().Be("agent-alpha");
        released.Should().Be(("runtime-root-1", "group-chat-reply|group-a|general|agent-alpha|evt-user-1|msg-user-1"));
    }

    private sealed class AwaitingGroupThreadCommandPort : RecordingGroupThreadCommandPort
    {
        private readonly TaskCompletionSource<AppendAgentMessageCommand> _appendTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<Aevatar.GroupChat.Abstractions.Commands.GroupCommandAcceptedReceipt> AppendAgentMessageAsync(
            AppendAgentMessageCommand command,
            CancellationToken ct = default)
        {
            _appendTcs.TrySetResult(command);
            return base.AppendAgentMessageAsync(command, ct);
        }

        public Task<AppendAgentMessageCommand> WaitForAppendAsync() => _appendTcs.Task;
    }

    private sealed class AwaitingGroupParticipantReplyProjectionPort : RecordingGroupParticipantReplyProjectionPort
    {
        private readonly TaskCompletionSource<(string actorId, string sessionId)> _releaseTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task ReleaseParticipantReplyProjectionAsync(
            string rootActorId,
            string sessionId,
            CancellationToken ct = default)
        {
            _releaseTcs.TrySetResult((rootActorId, sessionId));
            return base.ReleaseParticipantReplyProjectionAsync(rootActorId, sessionId, ct);
        }

        public Task<(string actorId, string sessionId)> WaitForReleaseAsync() => _releaseTcs.Task;
    }
}
