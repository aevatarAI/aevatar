using System.Text;
using System.Text.Json;
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
              "conversation": { "id": "conv-1", "platform_id": "oc_corr_1", "type": "group" },
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
    public void Parse_ShouldFallbackToConversationPlatformId_ForLarkGroupIdentity_WhenRawChatIdMissing()
    {
        var body = """
            {
              "message_id": "msg-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_platform_1", "type": "private" },
              "sender": { "platform_id": "user-1" },
              "content": { "type": "text", "text": "hi" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_group_1",
                    "chat_type": "group"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.Scope.Should().Be(ConversationScope.Group);
        parsed.Activity.Conversation.CanonicalKey.Should().Be("lark:group:oc_platform_1");
        parsed.Activity.Conversation.Partition.Should().Be("oc_platform_1");
        parsed.Activity.TransportExtras.NyxConversationId.Should().Be("route-uuid");
    }

    [Fact]
    public void Parse_ShouldUseLarkRawChatId_AsGroupCanonicalIdentity_WhenRouteConversationIsDefaultOrPrivate()
    {
        var body = """
            {
              "message_id": "msg-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-default-uuid", "type": "private" },
              "sender": { "platform_id": "user-42" },
              "content": { "type": "text", "text": "hi" },
              "raw_platform_data": {
                "event": {
                  "sender": {
                    "sender_id": {
                      "union_id": "on_user_42"
                    }
                  },
                  "message": {
                    "message_id": "om_group_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.Scope.Should().Be(ConversationScope.Group);
        parsed.Activity.Conversation.CanonicalKey.Should().Be("lark:group:oc_group_1");
        parsed.Activity.Conversation.Partition.Should().Be("oc_group_1");
        parsed.Activity.TransportExtras.NyxConversationId.Should().Be("route-default-uuid");
        parsed.Activity.TransportExtras.NyxPlatformMessageId.Should().Be("om_group_1");
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().Be("oc_group_1");
        parsed.Activity.TransportExtras.NyxLarkUnionId.Should().Be("on_user_42");
        parsed.Activity.Conversation.CanonicalKey.Should().NotContain("route-default-uuid");
        parsed.Activity.Conversation.CanonicalKey.Should().NotContain("user-42");
    }

    [Fact]
    public void Parse_ShouldRejectLarkGroup_WhenPlatformChatIdentityMissing()
    {
        var body = """
            {
              "message_id": "msg-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "type": "private" },
              "sender": {},
              "content": { "type": "text", "text": "hi" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_group_missing",
                    "chat_type": "group"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeFalse();
        parsed.Ignored.Should().BeTrue();
        parsed.ErrorCode.Should().Be("missing_lark_group_chat_identity");
    }

    [Fact]
    public void Parse_ShouldIgnorePayload_WhenTextContentIsEmpty()
    {
        var body = """
            {
              "message_id": "msg-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_123", "type": "group" },
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
    public void Parse_ShouldAcceptTextlessRelayAttachmentContent()
    {
        var body = """
            {
              "message_id": "msg-image-1",
              "platform": "telegram",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "123", "type": "private" },
              "sender": { "platform_id": "456", "display_name": "User One" },
              "content": {
                "type": "image",
                "attachments": [
                  {
                    "content_type": "image",
                    "url": "telegram-file-id-1",
                    "filename": "photo.png",
                    "mime_type": "image/png",
                    "size_bytes": 12345
                  }
                ]
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Content.Text.Should().BeEmpty();
        parsed.Activity.Content.Attachments.Should().ContainSingle();
        var attachment = parsed.Activity.Content.Attachments.Single();
        attachment.AttachmentId.Should().Be("telegram-file-id-1");
        attachment.Kind.Should().Be(AttachmentKind.Image);
        attachment.Name.Should().Be("photo.png");
        attachment.ContentType.Should().Be("image/png");
        attachment.BlobRef.Should().BeEmpty();
        attachment.ExternalUrl.Should().BeEmpty();
        attachment.SizeBytes.Should().Be(12345);
    }

    [Fact]
    public void Parse_ShouldKeepRelayAttachmentUrl_WhenItIsExternalHttpUrl()
    {
        var body = """
            {
              "message_id": "msg-file-url-1",
              "platform": "slack",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "C123", "type": "channel" },
              "sender": { "platform_id": "U456", "display_name": "User One" },
              "content": {
                "type": "file",
                "attachments": [
                  {
                    "content_type": "file",
                    "url": "https://files.example.test/report.pdf",
                    "filename": "report.pdf",
                    "mime_type": "application/pdf"
                  }
                ]
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Content.Attachments.Should().ContainSingle();
        var attachment = parsed.Activity.Content.Attachments.Single();
        attachment.AttachmentId.Should().Be("https://files.example.test/report.pdf");
        attachment.Kind.Should().Be(AttachmentKind.File);
        attachment.ExternalUrl.Should().Be("https://files.example.test/report.pdf");
        attachment.BlobRef.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldAcceptTextlessLarkImageKey_FromRawMessageContent()
    {
        var body = """
            {
              "message_id": "msg-lark-image-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "type": "private" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "image" },
              "raw_platform_data": {
                "event": {
                  "sender": {
                    "sender_id": {
                      "union_id": "on_user_1"
                    }
                  },
                  "message": {
                    "message_id": "om_image_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "image",
                    "content": "{\"image_key\":\"img_v3_abc\",\"file_name\":\"photo.png\",\"file_size\":42}"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.Scope.Should().Be(ConversationScope.Group);
        parsed.Activity.Conversation.CanonicalKey.Should().Be("lark:group:oc_group_1");
        parsed.Activity.Content.Text.Should().BeEmpty();
        parsed.Activity.Content.Attachments.Should().ContainSingle();
        var attachment = parsed.Activity.Content.Attachments.Single();
        attachment.AttachmentId.Should().Be("img_v3_abc");
        attachment.Kind.Should().Be(AttachmentKind.Image);
        attachment.Name.Should().Be("photo.png");
        attachment.ContentType.Should().Be("image");
        attachment.BlobRef.Should().BeEmpty();
        attachment.ExternalUrl.Should().BeEmpty();
        attachment.SizeBytes.Should().Be(42);
    }

    [Fact]
    public void Parse_ShouldAcceptTextlessLarkFileKey_FromRawMessageContentObject()
    {
        var body = """
            {
              "message_id": "msg-lark-file-1",
              "platform": "feishu",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "file", "text": "   " },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_file_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "file",
                    "content": {
                      "file_key": "file_v3_abc",
                      "file_name": "report.pdf",
                      "mime_type": "application/pdf",
                      "file_size": "2048"
                    }
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Content.Text.Should().BeEmpty();
        parsed.Activity.Content.Attachments.Should().ContainSingle();
        var attachment = parsed.Activity.Content.Attachments.Single();
        attachment.AttachmentId.Should().Be("file_v3_abc");
        attachment.Kind.Should().Be(AttachmentKind.File);
        attachment.Name.Should().Be("report.pdf");
        attachment.ContentType.Should().Be("application/pdf");
        attachment.BlobRef.Should().BeEmpty();
        attachment.SizeBytes.Should().Be(2048);
    }

    [Fact]
    public void Parse_ShouldDeduplicateLarkResourceUrlAndRawFileKeyAttachments()
    {
        var body = """
            {
              "message_id": "msg-lark-file-dedupe",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": {
                "type": "file",
                "text": "   ",
                "attachments": [
                  {
                    "content_type": "file",
                    "url": "https://open.larksuite.com/open-apis/im/v1/messages/om_file_1/resources/file_v3_abc?type=file",
                    "filename": "report.pdf",
                    "mime_type": "application/pdf"
                  }
                ]
              },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_file_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "file",
                    "content": {
                      "file_key": "file_v3_abc",
                      "file_name": "report.pdf",
                      "mime_type": "application/pdf"
                    }
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Content.Text.Should().BeEmpty();
        parsed.Activity.Content.Attachments.Should().ContainSingle();
        var attachment = parsed.Activity.Content.Attachments.Single();
        attachment.AttachmentId.Should().Be("https://open.larksuite.com/open-apis/im/v1/messages/om_file_1/resources/file_v3_abc?type=file");
        attachment.Kind.Should().Be(AttachmentKind.File);
        attachment.ExternalUrl.Should().Be("https://open.larksuite.com/open-apis/im/v1/messages/om_file_1/resources/file_v3_abc?type=file");
    }

    [Fact]
    public void Parse_ShouldIgnorePayload_WhenAttachmentIdentifiersAreMissing()
    {
        var body = """
            {
              "message_id": "msg-empty-attachment",
              "platform": "telegram",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "123", "type": "private" },
              "sender": { "platform_id": "456", "display_name": "User One" },
              "content": {
                "type": "image",
                "text": "   ",
                "attachments": [
                  { "content_type": "image", "filename": "photo.png" }
                ]
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeFalse();
        parsed.Ignored.Should().BeTrue();
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
    public void Parse_ShouldKeepDirectMessageCanonicalIdentity_WhenLarkChatTypeIsP2P()
    {
        var body = """
            {
              "message_id": "msg-dm-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-dm-uuid", "platform_id": "oc_dm_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "text", "text": "hello" },
              "raw_platform_data": {
                "event": {
                  "sender": {
                    "sender_id": {
                      "union_id": "on_user_1"
                    }
                  },
                  "message": {
                    "message_id": "om_dm_1",
                    "chat_id": "oc_dm_chat_1",
                    "chat_type": "p2p"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Conversation.Scope.Should().Be(ConversationScope.DirectMessage);
        parsed.Activity.Conversation.CanonicalKey.Should().Be("lark:dm:ou_user_1");
        parsed.Activity.Conversation.Partition.Should().Be("route-dm-uuid");
        parsed.Activity.TransportExtras.NyxConversationId.Should().Be("route-dm-uuid");
        parsed.Activity.TransportExtras.NyxPlatformMessageId.Should().Be("om_dm_1");
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().Be("oc_dm_chat_1");
        parsed.Activity.TransportExtras.NyxLarkUnionId.Should().Be("on_user_1");
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
        parsed.Activity!.Conversation.Scope.Should().Be(ConversationScope.Group);
        parsed.Activity.Conversation.CanonicalKey.Should().Be("lark:group:oc_123");
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
    public void Parse_ShouldExposeLarkOperatorIds_FromCardActionRawPlatformData()
    {
        var body = """
            {
              "message_id": "msg-card-operator-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_123", "type": "group" },
              "sender": { "platform_id": "ou_sender_123", "display_name": "User One" },
              "content": { "type": "card_action", "text": "{\"approved\":true}" },
              "raw_platform_data": {
                "event": {
                  "operator": {
                    "user_id": "ou_operator_1",
                    "open_id": "ou_open_operator_1",
                    "union_id": "on_operator_1"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxLarkOperatorUserId.Should().Be("ou_operator_1");
        parsed.Activity.TransportExtras.NyxLarkOperatorOpenId.Should().Be("ou_open_operator_1");
        parsed.Activity.TransportExtras.NyxLarkOperatorUnionId.Should().Be("on_operator_1");
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
                "text": "{\"value\":{\"agent_builder_action\":\"create_daily\",\"action_kind\":\"form_submit\"},\"form_value\":{\"github_username\":\"eanzhao\",\"schedule_time\":\"09:00\"}}"
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
            .WhoseValue.Should().Be("create_daily");
        cardAction.Arguments.Should().NotContainKey("action_kind");
        cardAction.ActionKind.Should().Be(ActionElementKind.FormSubmit);
        cardAction.FormFields.Should().ContainKey("github_username")
            .WhoseValue.Should().Be("eanzhao");
        cardAction.FormFields.Should().ContainKey("schedule_time")
            .WhoseValue.Should().Be("09:00");
        cardAction.ActionId.Should().Be("create_daily");
    }

    [Fact]
    public void Parse_ShouldPromoteNestedActionIdAndValue_FromLarkButtonClick()
    {
        // NyxID's Lark adapter (parse_card_action_event) relays button clicks as
        // {"tag":...,"value":<composed value object>,...}: the action identity sits
        // nested under `value`, not at the root. The transport must promote it into
        // the typed ActionId/SubmittedValue fields or generic buttons parse empty
        // and the turn runner drops the click.
        var body = """
            {
              "message_id": "msg-card-button-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"tag\":\"button\",\"value\":{\"action_id\":\"confirm_deploy\",\"value\":\"deploy-staging\",\"action_kind\":\"button\"},\"form_value\":null,\"open_message_id\":\"om_card_1\"}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.ActionId.Should().Be("confirm_deploy");
        cardAction.SubmittedValue.Should().Be("deploy-staging");
        cardAction.ActionKind.Should().Be(ActionElementKind.Button);
        // Mirror semantics: typed fields are populated while the boundary arguments stay
        // available for callback consumers that read the original payload.
        cardAction.Arguments.Should().ContainKey("action_id").WhoseValue.Should().Be("confirm_deploy");
        cardAction.Arguments.Should().ContainKey("value").WhoseValue.Should().Be("deploy-staging");
        cardAction.Arguments.Should().NotContainKey("action_kind");
    }

    [Fact]
    public void Parse_ShouldMapLarkBoundaryTagToTypedActionKind_WhenValueDoesNotCarryActionKind()
    {
        var body = """
            {
              "message_id": "msg-card-select-kind",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"tag\":\"select_static\",\"value\":{\"action_id\":\"environment\"},\"form_value\":{\"environment\":\"prod\"}}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.ActionKind.Should().Be(ActionElementKind.Select);
        cardAction.FormFields.Should().ContainKey("environment")
            .WhoseValue.Should().Be("prod");
    }

    [Fact]
    public void Parse_ShouldMirrorNestedLarkButtonValueIntoTypedFields_AndKeepArguments()
    {
        var body = """
            {
              "message_id": "msg-card-generic-button",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"tag\":\"button\",\"value\":{\"action_id\":\"confirm_deploy\",\"value\":\"staging\"}}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.ActionKind.Should().Be(ActionElementKind.Button);
        cardAction.ActionId.Should().Be("confirm_deploy");
        cardAction.SubmittedValue.Should().Be("staging");
        cardAction.Arguments.Should().Contain("action_id", "confirm_deploy");
        cardAction.Arguments.Should().Contain("value", "staging");
    }

    [Fact]
    public void Parse_ShouldPopulateCardAction_FromCompactTelegramPayload()
    {
        var body = """
            {
              "message_id": "msg-card-compact",
              "platform": "telegram",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "123", "type": "private" },
              "sender": { "platform_id": "456", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"a\":\"llm_select_service\",\"s\":\"chrono-llm-shared\",\"v\":{\"service_id\":\"chrono-llm-shared\"}}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.ActionId.Should().Be("llm_select_service");
        cardAction.SubmittedValue.Should().Be("chrono-llm-shared");
        cardAction.Arguments.Should().NotContainKey("service_id");
        cardAction.LlmSelection.Action.Should().Be("select_service");
        cardAction.LlmSelection.ServiceId.Should().Be("chrono-llm-shared");
    }

    [Theory]
    [InlineData("lp")]
    [InlineData("llm_apply_preset")]
    public void Parse_ShouldMapCompactApplyPresetActionId_ToTypedLlmSelection(string actionId)
    {
        var cardActionText = JsonSerializer.Serialize(new
        {
            a = actionId,
            s = "work-fast",
            v = new
            {
                preset_id = "work-fast",
            },
        });
        var body = $$"""
            {
              "message_id": "msg-card-compact-preset",
              "platform": "telegram",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "123", "type": "private" },
              "sender": { "platform_id": "456", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": {{JsonSerializer.Serialize(cardActionText)}}
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.ActionId.Should().Be(actionId);
        cardAction.SubmittedValue.Should().Be("work-fast");
        cardAction.Arguments.Should().NotContainKey("preset_id");
        cardAction.LlmSelection.Action.Should().Be("apply_preset");
        cardAction.LlmSelection.PresetId.Should().Be("work-fast");
    }

    [Fact]
    public void Parse_ShouldMapLlmSelectionPageFields_ToTypedPayload()
    {
        var body = """
            {
              "message_id": "msg-card-page",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"value\":{\"action_id\":\"llp\",\"value\":\"2\",\"llm_action\":\"list_page\",\"page\":2,\"display_mode\":\"route\"}}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var llmSelection = parsed.Activity!.Content.CardAction.LlmSelection;
        llmSelection.Action.Should().Be("list_page");
        llmSelection.Page.Should().Be(2);
        llmSelection.DisplayMode.Should().Be("route");
        parsed.Activity.Content.CardAction.Arguments.Should().NotContainKey("page");
        parsed.Activity.Content.CardAction.Arguments.Should().NotContainKey("display_mode");
    }

    [Fact]
    public void Parse_ShouldMapNyxIdApprovalFields_ToTypedPayloadAndRemoveAuthorityKeys()
    {
        var body = """
            {
              "message_id": "msg-card-nyx-approval",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"value\":{\"action_id\":\"nyxid-approval-approve\",\"action_kind\":\"button\",\"nyxid_approval_request_id\":\"nyx-approval-1\",\"nyxid_approval_approved\":true,\"external_note\":\"kept\"}}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.ActionId.Should().Be("nyxid-approval-approve");
        cardAction.ActionKind.Should().Be(ActionElementKind.Button);
        cardAction.NyxIdApproval.RequestId.Should().Be("nyx-approval-1");
        cardAction.NyxIdApproval.Approved.Should().BeTrue();
        cardAction.Arguments.Should().NotContainKey("nyxid_approval_request_id");
        cardAction.Arguments.Should().NotContainKey("nyxid_approval_approved");
        cardAction.Arguments.Should().ContainKey("external_note").WhoseValue.Should().Be("kept");
    }

    [Fact]
    public void Parse_ShouldMapKnownWorkflowCallbackFields_ToTypedPayload()
    {
        var body = """
            {
              "message_id": "msg-card-workflow",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-1", "platform_id": "oc_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"value\":{\"actor_id\":\"actor-1\",\"run_id\":\"run-1\",\"step_id\":\"step-1\",\"approved\":false},\"form_value\":{\"user_input\":\"needs work\"}}"
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        var cardAction = parsed.Activity!.Content.CardAction;
        cardAction.Should().NotBeNull();
        cardAction!.WorkflowResume.ActorId.Should().Be("actor-1");
        cardAction.WorkflowResume.RunId.Should().Be("run-1");
        cardAction.WorkflowResume.StepId.Should().Be("step-1");
        cardAction.WorkflowResume.Approved.Should().BeFalse();
        cardAction.WorkflowResume.UserInput.Should().Be("needs work");
        cardAction.Arguments.Should().NotContainKey("actor_id");
        cardAction.Arguments.Should().NotContainKey("run_id");
        cardAction.Arguments.Should().NotContainKey("step_id");
        cardAction.Arguments.Should().NotContainKey("approved");
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
        cardAction!.WorkflowResume.ActorId.Should().Be("actor-1");
        cardAction.WorkflowResume.RunId.Should().Be("run-1");
        cardAction.WorkflowResume.StepId.Should().Be("step-1");
        cardAction.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldNotLetCardActionMessageChatTypeOverrideConversationScope()
    {
        var body = """
            {
              "message_id": "msg-card-chat-type",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "conv-card-1", "platform_id": "oc_card_chat_1", "type": "private" },
              "sender": { "platform_id": "ou_1", "display_name": "User One" },
              "content": {
                "content_type": "card_action",
                "text": "{\"value\":{\"actor_id\":\"actor-1\",\"run_id\":\"run-1\",\"step_id\":\"step-1\"}}"
              },
              "raw_platform_data": {
                "event": {
                  "context": {
                    "open_chat_id": "oc_card_chat_1",
                    "open_message_id": "om_card_1"
                  },
                  "message": {
                    "chat_id": "oc_wrong_group_1",
                    "chat_type": "group"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Type.Should().Be(ActivityType.CardAction);
        parsed.Activity.Conversation.Scope.Should().Be(ConversationScope.DirectMessage);
        parsed.Activity.Conversation.CanonicalKey.Should().Be("lark:dm:ou_1");
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().Be("oc_card_chat_1");
        parsed.Activity.TransportExtras.NyxPlatformMessageId.Should().Be("om_card_1");
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
              "content": { "type": "text", "text": "/summary" },
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
        parsed.Activity.TransportExtras.DeliveryAddressId.Should().Be("oc_dm_chat_1");
        parsed.Activity.TransportExtras.DeliveryAddressType.Should().Be("chat_id");
        parsed.Activity.TransportExtras.DeliveryFallbackAddressId.Should().Be("on_user_1");
        parsed.Activity.TransportExtras.DeliveryFallbackAddressType.Should().Be("union_id");
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
                "text": "{\"value\":{\"agent_builder_action\":\"create_daily\"}}"
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
                    "value": { "agent_builder_action": "create_daily" }
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxLarkUnionId.Should().Be("on_user_2");
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().Be("oc_dm_chat_2");
        parsed.Activity.TransportExtras.DeliveryAddressId.Should().Be("oc_dm_chat_2");
        parsed.Activity.TransportExtras.DeliveryAddressType.Should().Be("chat_id");
        parsed.Activity.TransportExtras.DeliveryFallbackAddressId.Should().BeEmpty();
        parsed.Activity.TransportExtras.DeliveryFallbackAddressType.Should().BeEmpty();
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
              "content": { "type": "text", "text": "/summary" }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.TransportExtras.NyxLarkUnionId.Should().BeEmpty();
        parsed.Activity.TransportExtras.NyxLarkChatId.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldExtractTextAndImage_FromLarkPostRichTextMessage()
    {
        // 图文夹杂: mixing text + an inline image in Lark produces a `post` (rich-text)
        // message whose words and image_key both live inside the nested 2D `content`
        // array. NyxID cannot normalize it (content_type=unknown, text=None,
        // attachments=[]), forwarding only the verbatim post under raw_platform_data.
        // The transport must recover both from raw or the turn is dropped as empty_text
        // and the bot never replies.
        var body = """
            {
              "message_id": "msg-lark-post-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "unknown" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_post_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "post",
                    "content": "{\"title\":\"任务\",\"content\":[[{\"tag\":\"text\",\"text\":\"chronoai 全部仓库\"}],[{\"tag\":\"img\",\"image_key\":\"img_v3_post_1\"}],[{\"tag\":\"text\",\"text\":\"时区是新加坡的\"}]]}"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Ignored.Should().BeFalse();
        parsed.Activity!.Conversation.Scope.Should().Be(ConversationScope.Group);
        parsed.Activity.Content.Text.Should().Contain("任务");
        parsed.Activity.Content.Text.Should().Contain("chronoai 全部仓库");
        parsed.Activity.Content.Text.Should().Contain("时区是新加坡的");
        parsed.Activity.Content.Attachments.Should().ContainSingle();
        var attachment = parsed.Activity.Content.Attachments.Single();
        attachment.AttachmentId.Should().Be("img_v3_post_1");
        attachment.Kind.Should().Be(AttachmentKind.Image);
    }

    [Fact]
    public void Parse_ShouldExtractText_FromLocaleWrappedLarkPostRichTextMessage()
    {
        var body = """
            {
              "message_id": "msg-lark-post-locale",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "unknown" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_post_locale_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "post",
                    "content": "{\"zh_cn\":{\"title\":\"任务标题\",\"content\":[[{\"tag\":\"text\",\"text\":\"第一段\"}],[{\"tag\":\"at\",\"user_name\":\"小助手\"},{\"tag\":\"text\",\"text\":\" 第二段\"}]]}}"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Ignored.Should().BeFalse();
        parsed.Activity!.Content.Text.Should().Be("任务标题\n第一段\n@小助手 第二段");
    }

    [Fact]
    public void Parse_ShouldAcceptLarkPostMessage_WithFormattedTextButNoImage()
    {
        // A rich-text post without an image (formatting / @ / links only) still arrives
        // as text=None from NyxID, so it must not be silently dropped either.
        var body = """
            {
              "message_id": "msg-lark-post-2",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "unknown" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_post_2",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "post",
                    "content": "{\"content\":[[{\"tag\":\"at\",\"user_name\":\"小助手\"},{\"tag\":\"text\",\"text\":\" 请看 \"},{\"tag\":\"a\",\"text\":\"这个链接\",\"href\":\"https://example.test\"}]]}"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Ignored.Should().BeFalse();
        parsed.Activity!.Content.Text.Should().Be("@小助手 请看 这个链接");
        parsed.Activity.Content.Attachments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ShouldExtractMultipleImages_FromLarkPostRichTextMessage()
    {
        var body = """
            {
              "message_id": "msg-lark-post-3",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "unknown" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_post_3",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "post",
                    "content": "{\"content\":[[{\"tag\":\"text\",\"text\":\"两张图\"}],[{\"tag\":\"img\",\"image_key\":\"img_a\"},{\"tag\":\"img\",\"image_key\":\"img_b\"}]]}"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Content.Text.Should().Be("两张图");
        parsed.Activity.Content.Attachments.Should().HaveCount(2);
        parsed.Activity.Content.Attachments.Select(a => a.AttachmentId)
            .Should().BeEquivalentTo(new[] { "img_a", "img_b" });
    }

    [Fact]
    public void Parse_ShouldStillIgnoreLarkPostMessage_WithNoTextOrImage()
    {
        // A degenerate post with no extractable text and no media (e.g. emotion-only)
        // has nothing actionable; keep ignoring it rather than promoting a blank LLM turn.
        var body = """
            {
              "message_id": "msg-lark-post-empty",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "unknown" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_post_empty",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "post",
                    "content": "{\"content\":[[{\"tag\":\"emotion\",\"emoji_type\":\"SMILE\"}]]}"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Ignored.Should().BeTrue();
        parsed.Success.Should().BeFalse();
        parsed.ErrorCode.Should().Be("empty_text");
    }

    [Fact]
    public void Parse_ShouldPopulateMentions_FromLarkGroupMessageRawPlatformData()
    {
        // Group admission needs to know whether the bot was @-mentioned. NyxID forwards the
        // verbatim Lark event but never lifts event.message.mentions[] into the normalized
        // payload, so the transport must surface each mention's open_id into ChatActivity.mentions
        // for the runtime gate to match against the bot's own open_id.
        var body = """
            {
              "message_id": "msg-mention-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "text", "text": "@_user_1 在吗" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_group_mention_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "mentions": [
                      { "key": "@_user_1", "id": { "open_id": "ou_bot_1", "union_id": "on_bot_1" }, "name": "Aevatar" }
                    ]
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Mentions.Should().ContainSingle();
        var mention = parsed.Activity.Mentions.Single();
        mention.CanonicalId.Should().Be("ou_bot_1");
        mention.DisplayName.Should().Be("Aevatar");
    }

    [Fact]
    public void Parse_ShouldPopulateMentions_FromLarkPostMessageRawPlatformData()
    {
        var body = """
            {
              "message_id": "msg-post-mention-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "unknown" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_post_mention_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "message_type": "post",
                    "content": "{\"content\":[[{\"tag\":\"at\",\"user_name\":\"Aevatar\"},{\"tag\":\"text\",\"text\":\" 处理一下\"}]]}",
                    "mentions": [
                      { "key": "@_user_1", "id": { "open_id": "ou_bot_1", "union_id": "on_bot_1" }, "name": "Aevatar" }
                    ]
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Content.Text.Should().Be("@Aevatar 处理一下");
        parsed.Activity.Mentions.Should().ContainSingle();
        var mention = parsed.Activity.Mentions.Single();
        mention.CanonicalId.Should().Be("ou_bot_1");
        mention.DisplayName.Should().Be("Aevatar");
    }

    [Fact]
    public void Parse_ShouldPopulateReplyToActivityId_FromReplyToPlatformMessageId()
    {
        // A group reply that does not re-@ the bot is still "addressed to the bot" when its
        // parent is one of the bot's own messages. NyxID normalizes Lark's event.message.parent_id
        // into reply_to_platform_message_id; the transport must expose it on ChatActivity so the
        // runtime can match it against the conversation's bot-sent message ids.
        var body = """
            {
              "message_id": "msg-reply-1",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "text", "text": "好的" },
              "reply_to_platform_message_id": "om_bot_parent_1",
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_reply_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.ReplyToActivityId.Should().Be("om_bot_parent_1");
    }

    [Fact]
    public void Parse_ShouldExtractAllMentions_FromLarkGroupMessageWithMultipleAtMentions()
    {
        var body = """
            {
              "message_id": "msg-mention-many",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "text", "text": "@_user_1 @_user_2 看一下" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_group_mention_many",
                    "chat_id": "oc_group_1",
                    "chat_type": "group",
                    "mentions": [
                      { "key": "@_user_1", "id": { "open_id": "ou_bot_1" }, "name": "Aevatar" },
                      { "key": "@_user_2", "id": { "open_id": "ou_human_2" }, "name": "Bob" }
                    ]
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Mentions.Select(m => m.CanonicalId)
            .Should().BeEquivalentTo(new[] { "ou_bot_1", "ou_human_2" });
    }

    [Fact]
    public void Parse_ShouldLeaveMentionsEmpty_WhenLarkGroupMessageHasNoAtMention()
    {
        // The gate's whole purpose: a plain group message (no @-mention) yields no mentions, so
        // the runtime can ignore it. Pin that the transport does not invent mentions.
        var body = """
            {
              "message_id": "msg-no-mention",
              "platform": "lark",
              "agent": { "api_key_id": "api-key-1" },
              "conversation": { "id": "route-uuid", "platform_id": "oc_group_1", "type": "group" },
              "sender": { "platform_id": "ou_user_1", "display_name": "User One" },
              "content": { "type": "text", "text": "今天天气不错" },
              "raw_platform_data": {
                "event": {
                  "message": {
                    "message_id": "om_group_plain_1",
                    "chat_id": "oc_group_1",
                    "chat_type": "group"
                  }
                }
              }
            }
            """;

        var parsed = _transport.Parse(Encoding.UTF8.GetBytes(body));

        parsed.Success.Should().BeTrue();
        parsed.Activity!.Mentions.Should().BeEmpty();
        parsed.Activity.ReplyToActivityId.Should().BeEmpty();
    }
}
