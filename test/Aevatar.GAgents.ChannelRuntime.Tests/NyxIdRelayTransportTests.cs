using System.Text;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxIdRelayTransportTests
{
    private readonly NyxIdRelayTransport _transport = new();

    [Theory]
    [InlineData("private", ConversationScope.DirectMessage, false)]
    [InlineData("group", ConversationScope.Group, false)]
    [InlineData("channel", ConversationScope.Channel, false)]
    [InlineData("device", ConversationScope.Unspecified, true)]
    public void Parse_ShouldMapConversationTypeIntoChatActivity(
        string conversationType,
        ConversationScope expectedScope,
        bool expectIgnored)
    {
        var body = $$"""
            {
              "message_id": "msg-1",
              "platform": "slack",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "type": "{{conversationType}}" },
              "sender": { "platform_id": "user-1", "display_name": "User One" },
              "content": { "type": "text", "text": "hello" },
              "timestamp": "2026-04-23T12:00:00Z"
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Ignored.Should().Be(expectIgnored);
        if (expectIgnored)
        {
            parsed.Success.Should().BeFalse();
            parsed.ErrorCode.Should().Be("unsupported_conversation_type");
            return;
        }

        parsed.Success.Should().BeTrue();
        parsed.Activity.Should().NotBeNull();
        parsed.Activity!.Conversation.Scope.Should().Be(expectedScope);
        parsed.Activity.ChannelId.Value.Should().Be("slack");
        parsed.Activity.TransportExtras.NyxAgentApiKeyId.Should().Be("api-key-1");
        parsed.Activity.TransportExtras.NyxPlatform.Should().Be("slack");
        parsed.Activity.OutboundDelivery.ReplyMessageId.Should().Be("msg-1");
        parsed.Activity.RawPayloadBlobRef.Should().StartWith("slack-raw:");
    }

    [Fact]
    public void Parse_ShouldPropagateReplyTokenIntoOutboundDelivery()
    {
        var body = """
            {
              "message_id": "msg-relay-1",
              "platform": "lark",
              "reply_token": "  relay-access-token-xyz  ",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "type": "group" },
              "sender": { "platform_id": "user-1" },
              "content": { "type": "text", "text": "hi" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.OutboundDelivery.ReplyMessageId.Should().Be("msg-relay-1");
        parsed.Activity.OutboundDelivery.ReplyAccessToken.Should().Be("relay-access-token-xyz");
    }

    [Fact]
    public void Parse_ShouldFallbackToConversationPlatformId_WhenConversationIdMissing()
    {
        var body = """
            {
              "message_id": "msg-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "platform_id": "oc_platform_123", "type": "group" },
              "sender": { "platform_id": "user-1" },
              "content": { "type": "text", "text": "hi" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.CanonicalKey.Should().Be("lark:group:oc_platform_123");
        parsed.Activity.TransportExtras.NyxConversationId.Should().Be("oc_platform_123");
    }

    [Fact]
    public void Parse_ShouldFallbackToSenderId_WhenConversationIdentityAbsentAndScopeIsGroup()
    {
        var body = """
            {
              "message_id": "msg-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "type": "group" },
              "sender": { "platform_id": "user-42" },
              "content": { "type": "text", "text": "hi" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.CanonicalKey.Should().Be("lark:group:user-42");
    }

    [Fact]
    public void Parse_ShouldProduceNonEmptyCanonicalKey_WhenAllConversationIdentitiesMissing()
    {
        var body = """
            {
              "message_id": "msg-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "type": "group" },
              "sender": {},
              "content": { "type": "text", "text": "hi" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.CanonicalKey.Should().NotBeNullOrWhiteSpace();
        parsed.Activity.Conversation.CanonicalKey.Should().StartWith("lark:group:");
    }

    [Fact]
    public void Parse_ShouldIgnorePayload_WhenTextContentIsEmpty()
    {
        var body = """
            {
              "message_id": "msg-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "type": "group" },
              "sender": { "platform_id": "user-1" },
              "content": { "type": "text", "text": "   " }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Ignored.Should().BeTrue();
        parsed.Success.Should().BeFalse();
        parsed.ErrorCode.Should().Be("empty_text");
    }

    [Fact]
    public void Parse_ShouldUseSenderIdAsDirectMessageCanonicalTail()
    {
        var body = """
            {
              "message_id": "msg-dm-1",
              "platform": "discord",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-dm-1", "type": "private" },
              "sender": { "platform_id": "user-42", "display_name": "User Forty Two" },
              "content": { "type": "text", "text": "hello" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.CanonicalKey.Should().Be("discord:dm:user-42");
        parsed.Activity.TransportExtras.NyxConversationId.Should().Be("conv-dm-1");
    }

    [Fact]
    public void Parse_ShouldExposeLarkPlatformMessageId_FromRawPlatformData()
    {
        var body = """
            {
              "message_id": "msg-lark-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_123", "type": "group" },
              "sender": { "platform_id": "ou_123", "display_name": "User One" },
              "content": { "type": "text", "text": "hello" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_123"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxPlatformMessageId.Should().Be("om_123");
    }

    [Fact]
    public void Parse_ShouldPreferCurrentPlatformMessageId_WhenReplyTargetAlsoPresent()
    {
        var body = """
            {
              "message_id": "msg-card-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_123", "type": "group" },
              "sender": { "platform_id": "ou_123", "display_name": "User One" },
              "content": { "type": "card_action", "text": "{\"approved\":true}" },
              "reply_to_platform_message_id": "om_parent",
              "raw_platform_data": {
                "event": {
                  "context": {
                    "open_message_id": "om_raw"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxPlatformMessageId.Should().Be("om_raw");
    }
}
