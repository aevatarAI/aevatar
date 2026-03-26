using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Application.Workers;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class GroupMentionHintReplyLoopIntegrationTests
{
    [Fact]
    public async Task WorkerAndHandler_ShouldAppendReplyAfterReceivingHint()
    {
        var streams = new InMemoryStreamProvider();
        var queryPort = new StubGroupTimelineQueryPort
        {
            Thread = new GroupThreadSnapshot(
                "group-chat:thread:group-a:general",
                "group-a",
                "general",
                "General",
                ["agent-alpha"],
                [],
                [
                    new GroupTimelineMessageSnapshot(
                        "msg-user-1",
                        1,
                        GroupMessageSenderKind.User,
                        "user-1",
                        "hello @agent-alpha",
                        string.Empty,
                        ["agent-alpha"]),
                ],
                2,
                "evt-user-1",
                DateTimeOffset.Parse("2026-03-25T00:00:00+00:00")),
        };
        var commandPort = new AwaitingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort
        {
            Result = new ParticipantReplyGenerationResult("reply from integration"),
        };
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort();
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var handler = new GroupMentionHintReplyLoopHandler(queryPort, commandPort, replyPort, dispatchPort, projectionPort);
        var worker = new GroupMentionHintWorker(streams, handler);

        await using var subscription = await worker.SubscribeAsync("agent-alpha");
        await streams.GetStream(GroupMentionHintStreamIds.ForParticipant("agent-alpha")).ProduceAsync(
            new GroupMentionHint
            {
                GroupId = "group-a",
                ThreadId = "general",
                MessageId = "msg-user-1",
                ParticipantAgentId = "agent-alpha",
                SourceEventId = "evt-user-1",
                SourceStateVersion = 2,
                TimelineCursor = 1,
            });

        var append = await commandPort.WaitForAppendAsync();

        append.Text.Should().Be("reply from integration");
        append.ParticipantAgentId.Should().Be("agent-alpha");
        append.ReplyToMessageId.Should().Be("msg-user-1");
        append.MessageId.Should().Be("participant-reply:agent-alpha:evt-user-1");
        dispatchPort.Requests.Should().BeEmpty();
        projectionPort.EnsureCalls.Should().BeEmpty();
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
}
