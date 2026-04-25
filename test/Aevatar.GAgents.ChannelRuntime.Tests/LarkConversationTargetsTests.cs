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
    [InlineData("oc_group_chat_2", "chat_id")]
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

    [Fact]
    public void Resolve_ShouldUseTypedFieldsVerbatim_WhenBothPopulated()
    {
        var resolved = LarkConversationTargets.Resolve(
            typedReceiveId: "ou_user_1",
            typedReceiveIdType: "open_id",
            legacyConversationId: "oc_chat_1");

        resolved.ReceiveId.Should().Be("ou_user_1");
        resolved.ReceiveIdType.Should().Be("open_id");
        resolved.FellBackToPrefixInference.Should().BeFalse();
    }

    [Theory]
    [InlineData("", "open_id")]
    [InlineData("ou_user_1", "")]
    [InlineData(null, "open_id")]
    [InlineData("ou_user_1", null)]
    public void Resolve_ShouldFallBackToLegacyInference_WhenEitherTypedFieldIsBlank(
        string? typedReceiveId,
        string? typedReceiveIdType)
    {
        var resolved = LarkConversationTargets.Resolve(
            typedReceiveId,
            typedReceiveIdType,
            legacyConversationId: "ou_legacy_user");

        resolved.ReceiveId.Should().Be("ou_legacy_user");
        resolved.ReceiveIdType.Should().Be("open_id");
        resolved.FellBackToPrefixInference.Should().BeTrue();
    }

    [Fact]
    public void Resolve_ShouldFallBackToChatIdDefault_WhenLegacyConversationIdIsUnrecognized()
    {
        var resolved = LarkConversationTargets.Resolve(
            typedReceiveId: null,
            typedReceiveIdType: null,
            legacyConversationId: "om_legacy_message_id");

        resolved.ReceiveId.Should().Be("om_legacy_message_id");
        resolved.ReceiveIdType.Should().Be("chat_id");
        resolved.FellBackToPrefixInference.Should().BeTrue();
    }

    [Theory]
    [InlineData("p2p")]
    [InlineData("P2P")]
    [InlineData("direct_message")]
    [InlineData("DirectMessage")]
    [InlineData("dm")]
    public void BuildFromInbound_ShouldUseSenderOpenId_ForDirectMessages(string chatType)
    {
        // The relay puts the Lark sender open_id (`ou_*`) into ChannelInboundEvent.SenderId,
        // while ConversationId may be the route id or the DM's underlying `oc_*` chat_id —
        // neither of which Lark's outbound API will accept with receive_id_type=chat_id when
        // the bot is only authorised to DM the user, not the synthetic DM thread. Pin sender
        // open_id at delivery-target creation time.
        var target = LarkConversationTargets.BuildFromInbound(
            chatType,
            conversationId: "oc_dm_underlying_chat",
            senderId: "ou_user_1");

        target.ReceiveId.Should().Be("ou_user_1");
        target.ReceiveIdType.Should().Be("open_id");
        target.FellBackToPrefixInference.Should().BeFalse();
    }

    [Theory]
    [InlineData("group")]
    [InlineData("channel")]
    [InlineData("thread")]
    [InlineData("conversation")]
    [InlineData(null)]
    [InlineData("")]
    public void BuildFromInbound_ShouldUseConversationChatId_ForNonDirectMessages(string? chatType)
    {
        var target = LarkConversationTargets.BuildFromInbound(
            chatType,
            conversationId: "oc_group_chat_1",
            senderId: "ou_user_1");

        target.ReceiveId.Should().Be("oc_group_chat_1");
        target.ReceiveIdType.Should().Be("chat_id");
        target.FellBackToPrefixInference.Should().BeFalse();
    }

    [Fact]
    public void BuildFromInbound_ShouldFallBackToConversationChatId_WhenSenderIsMissingForDm()
    {
        // Defensive: if the relay payload omits the sender open_id for a DM, we cannot
        // reliably target the user. Returning the conversation_id (typed as chat_id) lets the
        // outbound proxy fail loudly with the actual Lark error rather than silently sending
        // to a wrong target.
        var target = LarkConversationTargets.BuildFromInbound(
            chatType: "p2p",
            conversationId: "oc_chat_1",
            senderId: "");

        target.ReceiveId.Should().Be("oc_chat_1");
        target.ReceiveIdType.Should().Be("chat_id");
    }
}
