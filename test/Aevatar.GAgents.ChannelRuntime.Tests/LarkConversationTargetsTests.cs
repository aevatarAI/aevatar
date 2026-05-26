using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Platform.Lark;

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

    [Fact]
    public void BuildFromInbound_ShouldPreferLarkChatId_ForP2pDirectMessages()
    {
        // chat_id (oc_*) is the literal DM thread between the user and the bot that received
        // the inbound event. When the outbound proxy authenticates as the SAME Lark app, sending
        // back via `receive_id_type=chat_id` targets the same chat WITHOUT traversing any user-id
        // resolution. This is the only path that survives both cross-app (`99992361`) and
        // cross-tenant (`99992364`) deployments where the relay-side ingress and outbound apps
        // share at least the inbound chat membership.
        var target = LarkConversationTargets.BuildFromInbound(
            chatType: "p2p",
            conversationId: "oc_dm_underlying_chat",
            senderId: "ou_user_1",
            larkUnionId: "on_user_1",
            larkChatId: "oc_dm_chat_1");

        target.ReceiveId.Should().Be("oc_dm_chat_1");
        target.ReceiveIdType.Should().Be("chat_id");
        target.FellBackToPrefixInference.Should().BeFalse();
    }

    [Fact]
    public void BuildFromInbound_ShouldFallBackToUnionId_ForP2pWithoutChatId()
    {
        // When the relay does not surface a Lark chat_id (older relay revisions, malformed
        // raw_platform_data), union_id is the next-best tenant-stable identifier. Lark rejects
        // it as `code:99992364 user id cross tenant` when the relay-side ingress and outbound
        // apps live in different tenants — flip FellBackToPrefixInference=true so call sites
        // LogDebug and operators can correlate the rejection with a missing-chat_id ingress.
        var target = LarkConversationTargets.BuildFromInbound(
            chatType: "p2p",
            conversationId: "oc_dm_underlying_chat",
            senderId: "ou_user_1",
            larkUnionId: "on_user_1");

        target.ReceiveId.Should().Be("on_user_1");
        target.ReceiveIdType.Should().Be("union_id");
        target.FellBackToPrefixInference.Should().BeTrue();
    }

    [Fact]
    public void BuildFromInbound_ShouldFallBackToSenderOpenId_ForP2pWithoutChatIdOrUnionId()
    {
        // Last-resort identifier. Surfaces `code:99992361 open_id cross app` when the
        // relay-side ingress and outbound apps differ — flip FellBack so call sites LogDebug
        // and operators see exactly which identifier they ended up with.
        var target = LarkConversationTargets.BuildFromInbound(
            chatType: "p2p",
            conversationId: "oc_dm_underlying_chat",
            senderId: "ou_user_1");

        target.ReceiveId.Should().Be("ou_user_1");
        target.ReceiveIdType.Should().Be("open_id");
        target.FellBackToPrefixInference.Should().BeTrue();
    }

    [Fact]
    public void BuildFromInbound_ShouldPreferLarkChatId_ForGroupChats()
    {
        // For groups/channels/threads, the inbound Lark `oc_*` chat_id is tenant-scoped and
        // cross-app safe (any app added to the chat can post via receive_id_type=chat_id).
        // Prefer it over the routing conversation_id, which may be a NyxID-internal route id
        // that Lark cannot resolve.
        var target = LarkConversationTargets.BuildFromInbound(
            chatType: "group",
            conversationId: "ddd-1234-route-id",
            senderId: "ou_user_1",
            larkUnionId: "on_user_1",
            larkChatId: "oc_group_chat_1");

        target.ReceiveId.Should().Be("oc_group_chat_1");
        target.ReceiveIdType.Should().Be("chat_id");
        target.FellBackToPrefixInference.Should().BeFalse();
    }

    [Theory]
    [InlineData("group")]
    [InlineData("channel")]
    [InlineData("thread")]
    [InlineData("conversation")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P2P")]
    [InlineData("direct_message")]
    [InlineData("dm")]
    public void BuildFromInbound_ShouldUseConversationChatId_ForNonP2pChatTypes(string? chatType)
    {
        // ChannelConversationTurnRunner.ResolveConversationChatType is the only emitter of
        // ChannelMetadataKeys.ChatType in this repo and produces exactly "p2p" for direct
        // messages. Anything else — group/channel/thread/blank, or even other casings of "p2p" —
        // is treated as a non-DM target and routed through `oc_*/chat_id`. Keep this guard
        // narrow until a second emitter actually lands.
        var target = LarkConversationTargets.BuildFromInbound(
            chatType,
            conversationId: "oc_group_chat_1",
            senderId: "ou_user_1");

        target.ReceiveId.Should().Be("oc_group_chat_1");
        target.ReceiveIdType.Should().Be("chat_id");
        target.FellBackToPrefixInference.Should().BeFalse();
    }

    [Fact]
    public void BuildFromInbound_ShouldReturnEmptyTypedPairWithFellBack_WhenP2pAndSenderIsMissing()
    {
        // Defensive: a confused inbound (chat_type=p2p but no sender open_id) cannot be pinned
        // to a typed receive target without re-creating the original /daily outage shape (open_id
        // typed as chat_id). Return an empty typed pair with FellBack=true so the outbound
        // resolver runs the legacy prefix path and call sites emit the Debug breadcrumb.
        var target = LarkConversationTargets.BuildFromInbound(
            chatType: "p2p",
            conversationId: "ou_user_1",
            senderId: "");

        target.ReceiveId.Should().BeEmpty();
        target.ReceiveIdType.Should().BeEmpty();
        target.FellBackToPrefixInference.Should().BeTrue();
    }

    [Fact]
    public void BuildFromInboundWithFallback_ShouldPairChatIdPrimary_WithUnionIdFallback_ForP2p()
    {
        // PR #412 reviewer concern (codex-bot, P1): chat_id-first regresses cross-app same-tenant
        // deployments where the outbound app is not a member of the inbound DM. The fallback
        // pairs the primary chat_id with a secondary union_id captured at ingress so the
        // runtime can retry once on `230002 bot not in chat` without needing a fresh ingress.
        var target = LarkConversationTargets.BuildFromInboundWithFallback(
            chatType: "p2p",
            conversationId: "oc_dm_chat_1",
            senderId: "ou_user_1",
            larkUnionId: "on_user_1",
            larkChatId: "oc_dm_chat_1");

        target.Primary.ReceiveId.Should().Be("oc_dm_chat_1");
        target.Primary.ReceiveIdType.Should().Be("chat_id");
        target.Fallback.Should().NotBeNull();
        target.Fallback!.Value.ReceiveId.Should().Be("on_user_1");
        target.Fallback.Value.ReceiveIdType.Should().Be("union_id");
    }

    [Fact]
    public void BuildFromInboundWithFallback_ShouldNotPairFallback_ForGroupChats()
    {
        // For groups chat_id is tenant-scoped; either the outbound app is in the chat (chat_id
        // works) or it isn't (no user-id-based identifier helps). No fallback to attempt.
        var target = LarkConversationTargets.BuildFromInboundWithFallback(
            chatType: "group",
            conversationId: "oc_group_chat_1",
            senderId: "ou_user_1",
            larkUnionId: "on_user_1",
            larkChatId: "oc_group_chat_1");

        target.Primary.ReceiveIdType.Should().Be("chat_id");
        target.Fallback.Should().BeNull();
    }

    [Fact]
    public void BuildFromInboundWithFallback_ShouldOmitFallback_WhenPrimaryIsNotChatId()
    {
        // When the primary already degrades to union_id or open_id (chat_id missing at
        // ingress), there is no further fallback to capture — the primary IS the safest
        // identifier we have.
        var target = LarkConversationTargets.BuildFromInboundWithFallback(
            chatType: "p2p",
            conversationId: "ou_legacy",
            senderId: "ou_user_1",
            larkUnionId: "on_user_1");

        target.Primary.ReceiveIdType.Should().Be("union_id");
        target.Fallback.Should().BeNull();
    }

    [Fact]
    public void Resolve_ShouldRecoverOpenIdForP2pConfusedInbound_ViaPrefixInference()
    {
        // Pairs with BuildFromInbound's defensive p2p+empty-sender path: the empty typed pair
        // makes Resolve fall back to the prefix heuristic on the legacy ConversationId, which
        // recovers `open_id` for an `ou_*` value. Net effect: confused inbound is observable
        // (FellBack=true) and still produces the right receive_id_type.
        var typed = LarkConversationTargets.BuildFromInbound(
            chatType: "p2p",
            conversationId: "ou_user_1",
            senderId: "");
        var resolved = LarkConversationTargets.Resolve(
            typed.ReceiveId,
            typed.ReceiveIdType,
            legacyConversationId: "ou_user_1");

        resolved.ReceiveId.Should().Be("ou_user_1");
        resolved.ReceiveIdType.Should().Be("open_id");
        resolved.FellBackToPrefixInference.Should().BeTrue();
    }
}
