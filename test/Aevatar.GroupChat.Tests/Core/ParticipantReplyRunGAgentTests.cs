using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Groups;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Core.GAgents;
using Aevatar.GroupChat.Tests.Application;
using Aevatar.GroupChat.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Core;

public sealed class ParticipantReplyRunGAgentTests
{
    [Fact]
    public async Task HandleStartAsync_ShouldGenerateReplyAppendAndAdvanceFeed()
    {
        var queryPort = new StubGroupTimelineQueryPort
        {
            Thread = CreateThreadSnapshot(),
        };
        var feedCommandPort = new RecordingAgentFeedCommandPort();
        var threadCommandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort
        {
            Result = new ParticipantReplyGenerationResult("reply from run actor"),
        };
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort();
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var eventStore = new InMemoryEventStore();
        var actorId = GroupChatActorIds.ParticipantReplyRun("group-a", "general", "agent-alpha", "evt-user-1");
        var agent = GroupChatTestKit.CreateStatefulAgent<ParticipantReplyRunGAgent, ParticipantReplyRunState>(
            eventStore,
            actorId,
            () => new ParticipantReplyRunGAgent(
                feedCommandPort,
                queryPort,
                threadCommandPort,
                replyPort,
                dispatchPort,
                projectionPort));
        await agent.ActivateAsync();

        await agent.HandleStartAsync(CreateStartCommand());

        threadCommandPort.AppendCalls.Should().ContainSingle();
        threadCommandPort.AppendCalls[0].MessageId.Should().Be("participant-reply:agent-alpha:evt-user-1");
        threadCommandPort.AppendCalls[0].Text.Should().Be("reply from run actor");
        feedCommandPort.AdvanceCalls.Should().ContainSingle();
        agent.State.Status.Should().Be(GroupParticipantReplyRunStatus.Completed);
        agent.State.ReplyMessageId.Should().Be("participant-reply:agent-alpha:evt-user-1");
    }

    [Fact]
    public async Task HandleStartAsync_ShouldRecordAwaitingCompletionWhenRuntimeDispatchAccepted()
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
        var feedCommandPort = new RecordingAgentFeedCommandPort();
        var threadCommandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort();
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort
        {
            Result = new ParticipantRuntimeDispatchResult(
                ParticipantRuntimeBackendKind.Service,
                ParticipantRuntimeCompletionMode.AsyncObserved,
                "runtime-root-1",
                "group-chat-reply|group-a|general|topic-a|agent-alpha|evt-user-1|msg-user-1",
                "participant-reply:agent-alpha:evt-user-1"),
        };
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var agent = GroupChatTestKit.CreateStatefulAgent<ParticipantReplyRunGAgent, ParticipantReplyRunState>(
            new InMemoryEventStore(),
            GroupChatActorIds.ParticipantReplyRun("group-a", "general", "agent-alpha", "evt-user-1"),
            () => new ParticipantReplyRunGAgent(
                feedCommandPort,
                queryPort,
                threadCommandPort,
                replyPort,
                dispatchPort,
                projectionPort));
        await agent.ActivateAsync();

        await agent.HandleStartAsync(CreateStartCommand());

        projectionPort.EnsureCalls.Should().ContainSingle()
            .Which.Should().Be(("runtime-root-1", "group-chat-reply|group-a|general|topic-a|agent-alpha|evt-user-1|msg-user-1"));
        feedCommandPort.AdvanceCalls.Should().ContainSingle();
        threadCommandPort.AppendCalls.Should().BeEmpty();
        agent.State.Status.Should().Be(GroupParticipantReplyRunStatus.AwaitingCompletion);
        agent.State.SessionId.Should().Be("group-chat-reply|group-a|general|topic-a|agent-alpha|evt-user-1|msg-user-1");
    }

    [Fact]
    public async Task HandleCompleteAsync_ShouldReleaseProjectionWhenContentIsEmpty()
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
        var feedCommandPort = new RecordingAgentFeedCommandPort();
        var threadCommandPort = new RecordingGroupThreadCommandPort();
        var replyPort = new StubParticipantReplyGenerationPort();
        var dispatchPort = new RecordingParticipantRuntimeDispatchPort
        {
            Result = new ParticipantRuntimeDispatchResult(
                ParticipantRuntimeBackendKind.Service,
                ParticipantRuntimeCompletionMode.AsyncObserved,
                "runtime-root-1",
                "group-chat-reply|group-a|general|topic-a|agent-alpha|evt-user-1|msg-user-1",
                "participant-reply:agent-alpha:evt-user-1"),
        };
        var projectionPort = new RecordingGroupParticipantReplyProjectionPort();
        var agent = GroupChatTestKit.CreateStatefulAgent<ParticipantReplyRunGAgent, ParticipantReplyRunState>(
            new InMemoryEventStore(),
            GroupChatActorIds.ParticipantReplyRun("group-a", "general", "agent-alpha", "evt-user-1"),
            () => new ParticipantReplyRunGAgent(
                feedCommandPort,
                queryPort,
                threadCommandPort,
                replyPort,
                dispatchPort,
                projectionPort));
        await agent.ActivateAsync();
        await agent.HandleStartAsync(CreateStartCommand());

        await agent.HandleCompleteAsync(
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
                Content = string.Empty,
                TopicId = "topic-a",
            });

        projectionPort.ReleaseCalls.Should().ContainSingle()
            .Which.Should().Be(("runtime-root-1", "group-chat-reply|group-a|general|topic-a|agent-alpha|evt-user-1|msg-user-1"));
        threadCommandPort.AppendCalls.Should().BeEmpty();
        agent.State.Status.Should().Be(GroupParticipantReplyRunStatus.NoContent);
    }

    private static StartParticipantReplyRunCommand CreateStartCommand() =>
        new()
        {
            GroupId = "group-a",
            ThreadId = "general",
            ParticipantAgentId = "agent-alpha",
            SignalId = "msg-user-1",
            SourceEventId = "evt-user-1",
            SourceStateVersion = 2,
            TimelineCursor = 1,
            TopicId = "topic-a",
        };

    private static GroupThreadSnapshot CreateThreadSnapshot(
        IReadOnlyList<GroupParticipantRuntimeBindingSnapshot>? runtimeBindings = null)
    {
        return new GroupThreadSnapshot(
            "group-chat:thread:group-a:general",
            "group-a",
            "general",
            "General",
            ["agent-alpha"],
            runtimeBindings ?? [],
            [
                new GroupTimelineMessageSnapshot(
                    "msg-user-1",
                    1,
                    GroupMessageSenderKind.User,
                    "user-1",
                    "hello @agent-alpha",
                    string.Empty,
                    ["agent-alpha"],
                    "topic-a",
                    GroupSignalKind.Question),
            ],
            2,
            "evt-user-1",
            DateTimeOffset.Parse("2026-03-25T00:00:00+00:00"));
    }
}
