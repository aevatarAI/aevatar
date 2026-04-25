using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkConversationTargetsTests
{
    [Theory]
    [InlineData("ou_937adb03f3538c5e041bb3034c4e348e", "open_id")]
    [InlineData("ou_short", "open_id")]
    [InlineData("on_union_user_token", "union_id")]
    [InlineData("oc_group_chat_1", "chat_id")]
    [InlineData("oc_dm_thread", "chat_id")]
    public void ResolveReceiveIdType_ShouldMapKnownPrefixes(string conversationId, string expected)
    {
        LarkConversationTargets.ResolveReceiveIdType(conversationId).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("om_message_id")]
    [InlineData("user@example.com")]
    [InlineData("12345678")]
    public void ResolveReceiveIdType_ShouldFallBackToChatId_ForUnknownOrEmptyInput(string? conversationId)
    {
        LarkConversationTargets.ResolveReceiveIdType(conversationId).Should().Be("chat_id");
    }

    [Fact]
    public void ResolveReceiveIdType_ShouldTrimWhitespaceBeforeMatching()
    {
        LarkConversationTargets.ResolveReceiveIdType("  ou_user_1  ").Should().Be("open_id");
        LarkConversationTargets.ResolveReceiveIdType("\toc_chat_1\n").Should().Be("chat_id");
    }

    [Fact]
    public void ResolveReceiveIdType_ShouldBeCaseSensitive()
    {
        // Lark IDs are produced by Lark and are always lower-case. Treat any
        // case mismatch as unknown rather than guessing a mapping.
        LarkConversationTargets.ResolveReceiveIdType("OU_user_1").Should().Be("chat_id");
        LarkConversationTargets.ResolveReceiveIdType("ON_user_1").Should().Be("chat_id");
    }
}
