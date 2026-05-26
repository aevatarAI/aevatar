using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationGAgentActorIdsTests
{
    [Fact]
    public void BuildActorId_ShouldTrimCanonicalKey()
    {
        var actorId = ConversationGAgent.BuildActorId("  lark:dm:user-1  ");

        actorId.Should().Be("channel-conversation:lark:dm:user-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildActorId_ShouldRejectNullOrWhitespaceCanonicalKey(string? canonicalKey)
    {
        var act = () => ConversationGAgent.BuildActorId(canonicalKey!);

        act.Should().Throw<ArgumentException>();
    }
}
