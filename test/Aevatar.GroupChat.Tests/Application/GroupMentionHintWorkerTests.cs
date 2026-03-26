using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Ports;
using Aevatar.GroupChat.Application.Workers;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class GroupMentionHintWorkerTests
{
    [Fact]
    public async Task SubscribeAsync_ShouldReceiveParticipantHintsFromStream()
    {
        var streams = new InMemoryStreamProvider();
        var handler = new AwaitingMentionHintHandler();
        var worker = new GroupMentionHintWorker(streams, handler);

        await using var subscription = await worker.SubscribeAsync("agent-alpha");
        await streams.GetStream(GroupMentionHintStreamIds.ForParticipant("agent-alpha")).ProduceAsync(
            new GroupMentionHint
            {
                GroupId = "group-a",
                ThreadId = "general",
                MessageId = "msg-1",
                ParticipantAgentId = "agent-alpha",
                SourceEventId = "evt-1",
                SourceStateVersion = 2,
                TimelineCursor = 1,
            });

        var hint = await handler.WaitAsync();

        hint.ParticipantAgentId.Should().Be("agent-alpha");
        hint.SourceEventId.Should().Be("evt-1");
    }

    private sealed class AwaitingMentionHintHandler : IGroupMentionHintHandler
    {
        private readonly TaskCompletionSource<GroupMentionHint> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task HandleAsync(GroupMentionHint hint, CancellationToken ct = default)
        {
            _tcs.TrySetResult(hint);
            return Task.CompletedTask;
        }

        public Task<GroupMentionHint> WaitAsync() => _tcs.Task;
    }
}
