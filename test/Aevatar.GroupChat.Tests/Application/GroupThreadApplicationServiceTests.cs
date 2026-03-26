using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Application.Services;
using Aevatar.GroupChat.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GroupChat.Tests.Application;

public sealed class GroupThreadApplicationServiceTests
{
    [Fact]
    public async Task PostUserMessageAsync_ShouldEnsureProjectionAndDispatchCommandEnvelope()
    {
        var runtime = new RecordingActorRuntime();
        var dispatchPort = new RecordingActorDispatchPort();
        var projectionPort = new RecordingGroupTimelineProjectionPort();
        var service = new GroupThreadCommandApplicationService(runtime, dispatchPort, projectionPort);

        var receipt = await service.PostUserMessageAsync(
            GroupChatTestKit.CreateUserMessageCommand(directHintAgentIds: ["agent-alpha"]));

        projectionPort.EnsuredActorIds.Should().ContainSingle()
            .Which.Should().Be("group-chat:thread:group-a:general");
        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].actorId.Should().Be("group-chat:thread:group-a:general");
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be("group-chat:thread:group-a:general");
        dispatchPort.Calls[0].envelope.Payload.Should().NotBeNull();
        dispatchPort.Calls[0].envelope.Payload!.Is(PostUserMessageCommand.Descriptor).Should().BeTrue();
        receipt.TargetActorId.Should().Be("group-chat:thread:group-a:general");
        receipt.CorrelationId.Should().Be("group-a:general:msg-user-1");
    }

    [Fact]
    public async Task QueryService_ShouldDelegateToTimelineQueryPort()
    {
        var snapshot = new Aevatar.GroupChat.Abstractions.Queries.GroupThreadSnapshot(
            "group-chat:thread:group-a:general",
            "group-a",
            "general",
            "General",
            ["agent-alpha"],
            [],
            [],
            1,
            "evt-1",
            DateTimeOffset.Parse("2026-03-25T00:00:00+00:00"));
        var queryPort = new StubGroupTimelineQueryPort
        {
            Thread = snapshot,
            MentionedMessages =
            [
                new Aevatar.GroupChat.Abstractions.Queries.GroupTimelineMessageSnapshot(
                    "msg-1",
                    1,
                    GroupMessageSenderKind.User,
                    "user-1",
                    "hello",
                    string.Empty,
                    ["agent-alpha"]),
            ],
        };
        var service = new GroupThreadQueryApplicationService(queryPort);

        var thread = await service.GetThreadAsync("group-a", "general");
        var mentions = await service.GetMentionedMessagesAsync("group-a", "general", "agent-alpha", 0);

        thread.Should().BeSameAs(snapshot);
        mentions.Should().ContainSingle();
    }
}
