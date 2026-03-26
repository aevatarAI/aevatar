using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Participants;
using Aevatar.GroupChat.Hosting.Configuration;
using Aevatar.GroupChat.Hosting.Participants;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GroupChat.Tests.Hosting;

public sealed class DemoParticipantReplyGenerationPortTests
{
    [Fact]
    public async Task GenerateReplyAsync_ShouldReturnNull_WhenDemoReplyGenerationDisabled()
    {
        var port = CreatePort(enableDemoReplyGeneration: false, ["agent-alpha"]);

        var result = await port.GenerateReplyAsync(CreateRequest("agent-alpha"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task GenerateReplyAsync_ShouldReturnReply_WhenParticipantConfigured()
    {
        var port = CreatePort(enableDemoReplyGeneration: true, ["agent-alpha"]);

        var result = await port.GenerateReplyAsync(CreateRequest("agent-alpha"));

        result.Should().NotBeNull();
        result!.ReplyText.Should().Contain("agent-alpha");
        result.ReplyText.Should().Contain("你好，@agent-alpha");
    }

    private static DemoParticipantReplyGenerationPort CreatePort(
        bool enableDemoReplyGeneration,
        IReadOnlyList<string> participantAgentIds)
    {
        return new DemoParticipantReplyGenerationPort(
            Options.Create(new GroupChatCapabilityOptions
            {
                EnableDemoReplyGeneration = enableDemoReplyGeneration,
                ParticipantAgentIds = [.. participantAgentIds],
            }));
    }

    private static ParticipantReplyGenerationRequest CreateRequest(string participantAgentId)
    {
        return new ParticipantReplyGenerationRequest(
            "group-1",
            "thread-1",
            participantAgentId,
            "evt-1",
            1,
            1,
            new Aevatar.GroupChat.Abstractions.Queries.GroupTimelineMessageSnapshot(
                "msg-1",
                1,
                GroupMessageSenderKind.User,
                "user-1",
                "你好，@agent-alpha",
                string.Empty,
                [participantAgentId]),
            new Aevatar.GroupChat.Abstractions.Queries.GroupThreadSnapshot(
                "group-thread:group-1:thread-1",
                "group-1",
                "thread-1",
                "thread-1",
                [participantAgentId],
                [],
                [],
                1,
                "evt-1",
                DateTimeOffset.UtcNow));
    }
}
