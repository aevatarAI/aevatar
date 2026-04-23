using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayConversationTypeMapTests
{
    [Theory]
    [InlineData("private", ConversationScope.DirectMessage)]
    [InlineData("group", ConversationScope.Group)]
    [InlineData("channel", ConversationScope.Channel)]
    public void TryMap_ShouldResolveSupportedConversationTypes(string rawType, ConversationScope expected)
    {
        var mapped = NyxIdRelayConversationTypeMap.TryMap(rawType, out var scope);

        mapped.Should().BeTrue();
        scope.Should().Be(expected);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("unknown")]
    [InlineData("")]
    public void TryMap_ShouldRejectUnsupportedConversationTypes(string rawType)
    {
        var mapped = NyxIdRelayConversationTypeMap.TryMap(rawType, out var scope);

        mapped.Should().BeFalse();
        scope.Should().Be(ConversationScope.Unspecified);
    }
}
