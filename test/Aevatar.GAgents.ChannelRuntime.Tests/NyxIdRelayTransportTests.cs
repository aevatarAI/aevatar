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
    public void Parse_ShouldPropagateCorrelationIntoOutboundDelivery()
    {
        var body = """
            {
              "message_id": "msg-relay-1",
              "correlation_id": "corr-relay-1",
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
        parsed.Activity.OutboundDelivery.CorrelationId.Should().Be("corr-relay-1");
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

    [Fact]
    public void Parse_ShouldPopulateCardAction_ForAgentBuilderFormSubmit()
    {
        var body = """
            {
              "message_id": "msg-card-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"value\":{\"agent_builder_action\":\"create_daily_report\"},\"form_value\":{\"github_username\":\"eanzhao\",\"schedule_time\":\"09:00\"}}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Type.Should().Be(ActivityType.CardAction);
        parsed.Activity.Content.Text.Should().BeEmpty();
        var cardAction = parsed.Activity.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.Arguments.Should().ContainKey("agent_builder_action")
            .WhoseValue.Should().Be("create_daily_report");
        cardAction.FormFields.Should().ContainKey("github_username")
            .WhoseValue.Should().Be("eanzhao");
        cardAction.FormFields.Should().ContainKey("schedule_time")
            .WhoseValue.Should().Be("09:00");
        cardAction.ActionId.Should().Be("create_daily_report");
    }

    [Fact]
    public void Parse_ShouldAcceptEmptyCardActionText_AsEmptySubmission()
    {
        var body = """
            {
              "message_id": "msg-card-empty",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": { "content_type": "card_action", "text": "   " }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Type.Should().Be(ActivityType.CardAction);
        parsed.Activity.Content.CardAction.Should().NotBeNull();
        parsed.Activity.Content.CardAction!.Arguments.Should().BeEmpty();
        parsed.Activity.Content.CardAction.FormFields.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldAcceptCardAction_WhenConversationTypeIsMissing()
    {
        var body = """
            {
              "message_id": "msg-card-no-type",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"value\":{\"actor_id\":\"actor-1\",\"run_id\":\"run-1\",\"step_id\":\"step-1\"}}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Type.Should().Be(ActivityType.CardAction);
        parsed.Activity.Conversation.Scope.Should().Be(ConversationScope.Unspecified);
        var cardAction = parsed.Activity.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.Arguments.Should().ContainKey("actor_id").WhoseValue.Should().Be("actor-1");
        cardAction.Arguments.Should().ContainKey("run_id").WhoseValue.Should().Be("run-1");
        cardAction.Arguments.Should().ContainKey("step_id").WhoseValue.Should().Be("step-1");
    }

    [Fact]
    public void Parse_ShouldStillIgnoreTextMessage_WhenConversationTypeIsUnsupported()
    {
        var body = """
            {
              "message_id": "msg-device",
              "platform": "slack",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "platform_id": "device-1", "type": "device" },
              "content": { "type": "text", "text": "hello" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeFalse();
        parsed.Ignored.Should().BeTrue();
        parsed.ErrorCode.Should().Be("unsupported_conversation_type");
    }

    [Fact]
    public void Parse_ShouldReportIgnored_ForCardActionWithNonJsonText()
    {
        var body = """
            {
              "message_id": "msg-card-bad",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": { "content_type": "card_action", "text": "not json" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeFalse();
        parsed.Ignored.Should().BeTrue();
        parsed.ErrorCode.Should().Be("invalid_card_action_payload");
    }

    [Fact]
    public void Parse_ShouldExposeLarkUnionIdAndChatId_FromMessageReceiveRawPlatformData()
    {
        // Cross-app outbound delivery (issue: `code:99992361 open_id cross app`) needs the
        // tenant-stable `union_id` and the inbound `chat_id` to be visible at the agent-builder
        // layer. Both live inside the original Lark `im.message.receive_v1` event payload that
        // NyxID forwards verbatim under `raw_platform_data`.
        var body = """
            {
              "message_id": "msg-lark-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "text", "text": "/daily" },
              "raw_platform_data": {
                "schema": "2.0",
                "header": { "event_type": "im.message.receive_v1" },
                "event": {
                  "sender": {
                    "sender_id": {
                      "open_id": "ou_user_1",
                      "union_id": "on_user_1",
                      "user_id": "u123"
                    }
                  },
                  "message": {
                    "message_id": "om_lark_1",
                    "chat_id": "oc_dm_chat_1",
                    "chat_type": "p2p"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxLarkUnionId.Should().Be("on_user_1");
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().Be("oc_dm_chat_1");
    }

    [Fact]
    public void Parse_ShouldExposeLarkUnionIdAndChatId_FromCardActionRawPlatformData()
    {
        // Lark card submissions arrive as `card.action.trigger`, which carries the operator's
        // identifiers under `event.operator` (no `sender_id` envelope) and the chat under
        // `event.context.open_chat_id`. Verify both are surfaced on TransportExtras so the
        // post-submit agent-builder branch can consume the cross-app safe pair.
        var body = """
            {
              "message_id": "msg-card-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-2", "platform_id": "oc_chat_2", "type": "private" },
              "sender": { "platform_id": "ou_user_2", "display_name": "User Two" },
              "content": {
                "content_type": "card_action",
                "text": "{\"value\":{\"agent_builder_action\":\"create_daily_report\"}}"
              },
              "raw_platform_data": {
                "schema": "2.0",
                "header": { "event_type": "card.action.trigger" },
                "event": {
                  "operator": {
                    "open_id": "ou_user_2",
                    "union_id": "on_user_2",
                    "user_id": "u456"
                  },
                  "context": {
                    "open_chat_id": "oc_dm_chat_2",
                    "open_message_id": "om_card_2"
                  },
                  "action": {
                    "tag": "button",
                    "value": { "agent_builder_action": "create_daily_report" }
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxLarkUnionId.Should().Be("on_user_2");
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().Be("oc_dm_chat_2");
    }

    [Fact]
    public void Parse_ShouldLeaveLarkExtrasEmpty_ForNonLarkPlatform()
    {
        // The lark_* TransportExtras fields are intentionally Lark-specific: other platforms
        // populate their own platform-native equivalents. Pin the empty contract for non-Lark
        // traffic so a future refactor that broadens the parser cannot accidentally write
        // Lark IDs onto Telegram / Discord activities.
        var body = """
            {
              "message_id": "msg-tg-1",
              "platform": "telegram",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-tg-1", "platform_id": "12345", "type": "private" },
              "sender": { "platform_id": "tg-user-1", "display_name": "User TG" },
              "content": { "type": "text", "text": "hi" },
              "raw_platform_data": {
                "event": {
                  "sender": { "sender_id": { "union_id": "should_not_propagate" } },
                  "message": { "chat_id": "should_not_propagate" }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxLarkUnionId.Should().BeEmpty();
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldLeaveLarkExtrasEmpty_WhenRawPlatformDataMissing()
    {
        var body = """
            {
              "message_id": "msg-lark-2",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-3", "platform_id": "oc_chat_3", "type": "private" },
              "sender": { "platform_id": "ou_user_3", "display_name": "User Three" },
              "content": { "type": "text", "text": "/daily" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxLarkUnionId.Should().BeEmpty();
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().BeEmpty();
    }
}
