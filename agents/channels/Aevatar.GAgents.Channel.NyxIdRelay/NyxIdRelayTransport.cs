using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public NyxIdRelayParseResult Parse(byte[] bodyBytes)
    {
        ArgumentNullException.ThrowIfNull(bodyBytes);

        NyxIdRelayCallbackPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<NyxIdRelayCallbackPayload>(bodyBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return NyxIdRelayParseResult.Invalid("invalid_relay_payload", "Failed to parse relay payload.");
        }

        if (payload is null)
            return NyxIdRelayParseResult.Invalid("missing_relay_payload", "Relay payload is required.");

        if (string.IsNullOrWhiteSpace(payload.MessageId))
            return NyxIdRelayParseResult.Invalid("missing_message_id", "Relay payload is missing message_id.");

        var normalizedContentType = NormalizeContentType(payload.Content);
        var isCardAction = string.Equals(normalizedContentType, CardActionContentType, StringComparison.Ordinal);

        var text = payload.Content?.Text?.Trim();
        if (!isCardAction && string.IsNullOrWhiteSpace(text))
            return NyxIdRelayParseResult.IgnoredPayload(payload, "empty_text", "Relay payload does not contain text content.");

        CardActionSubmission? cardAction = null;
        if (isCardAction)
        {
            cardAction = BuildCardActionSubmission(text, payload);
            if (cardAction is null)
            {
                return NyxIdRelayParseResult.IgnoredPayload(
                    payload,
                    "invalid_card_action_payload",
                    "Relay card_action payload text is not a JSON object.");
            }
        }

        var conversationType = payload.Conversation?.Type ?? payload.Conversation?.ConversationType;
        if (!NyxIdRelayConversationTypeMap.TryMap(conversationType, out var scope))
        {
            if (!isCardAction)
            {
                return NyxIdRelayParseResult.IgnoredPayload(
                    payload,
                    "unsupported_conversation_type",
                    $"Relay conversation.type '{conversationType ?? "<empty>"}' is not supported by channel runtime.");
            }

            // Card actions carry their own correlation keys (actor_id/run_id/step_id or
            // agent_builder_action) and aren't anchored to a specific conversation scope.
            // When the relay omits or uses an unfamiliar conversation.type for a card
            // submission, keep the activity flowing with an Unspecified scope so downstream
            // routing still sees the typed CardActionSubmission.
            scope = ConversationScope.Unspecified;
        }

        var platform = NormalizePlatform(payload.Platform);
        var conversationIdentity = ResolveConversationIdentity(platform, payload);
        var senderId = payload.Sender?.PlatformId?.Trim();
        var canonicalKey = BuildCanonicalKey(platform, scope, conversationIdentity, senderId);
        var partition = conversationIdentity;
        var timestamp = ParseTimestamp(payload.Timestamp);
        var botId = payload.Agent?.ApiKeyId?.Trim();
        var platformMessageId = ResolvePlatformMessageId(payload, platform);
        var correlationId = string.IsNullOrWhiteSpace(payload.CorrelationId)
            ? payload.MessageId.Trim()
            : payload.CorrelationId.Trim();

        var content = new MessageContent
        {
            Text = isCardAction ? string.Empty : text ?? string.Empty,
        };
        if (cardAction is not null)
            content.CardAction = cardAction;

        var activity = new ChatActivity
        {
            Id = payload.MessageId.Trim(),
            Type = isCardAction ? ActivityType.CardAction : ActivityType.Message,
            ChannelId = ChannelId.From(platform),
            Bot = BotInstanceId.From(string.IsNullOrWhiteSpace(botId) ? "nyx-relay-bot" : botId),
            Conversation = ConversationReference.Create(
                ChannelId.From(platform),
                BotInstanceId.From(string.IsNullOrWhiteSpace(botId) ? "nyx-relay-bot" : botId),
                scope,
                partition,
                BuildScopeSegment(scope),
                scope == ConversationScope.DirectMessage && !string.IsNullOrWhiteSpace(senderId)
                    ? senderId
                    : conversationIdentity),
            From = new ParticipantRef
            {
                CanonicalId = senderId ?? string.Empty,
                DisplayName = payload.Sender?.DisplayName?.Trim() ?? string.Empty,
            },
            Timestamp = Timestamp.FromDateTimeOffset(timestamp),
            Content = content,
            RawPayloadBlobRef = $"{platform}-raw:{ComputeBodyHash(bodyBytes)}",
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = payload.MessageId.Trim(),
                CorrelationId = correlationId,
            },
            TransportExtras = new TransportExtras
            {
                NyxMessageId = payload.MessageId.Trim(),
                NyxAgentApiKeyId = payload.Agent?.ApiKeyId?.Trim() ?? string.Empty,
                NyxPlatform = platform,
                NyxConversationId = payload.Conversation?.Id?.Trim() ?? conversationIdentity,
                NyxPlatformMessageId = platformMessageId,
                NyxLarkUnionId = ExtractLarkUnionId(platform, payload, isCardAction),
                NyxLarkChatId = ExtractLarkChatId(platform, payload, isCardAction),
            },
        };

        activity.Conversation.CanonicalKey = canonicalKey;
        return NyxIdRelayParseResult.Parsed(payload, activity);
    }

    private const string CardActionContentType = "card_action";

    private static string NormalizeContentType(NyxIdRelayContentPayload? content)
    {
        var value = content?.ContentType;
        if (string.IsNullOrWhiteSpace(value))
            value = content?.Type;
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static CardActionSubmission? BuildCardActionSubmission(string? rawText, NyxIdRelayCallbackPayload payload)
    {
        var submission = new CardActionSubmission();
        if (!string.IsNullOrWhiteSpace(payload.PlatformMessageId))
            submission.SourceMessageId = payload.PlatformMessageId.Trim();

        if (string.IsNullOrWhiteSpace(rawText))
            return submission;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(rawText);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (TryReadString(root, "action_id", out var actionId))
            submission.ActionId = actionId;
        if (TryReadString(root, "submitted_value", out var submittedValue))
            submission.SubmittedValue = submittedValue;
        if (string.IsNullOrEmpty(submission.SourceMessageId) &&
            TryReadString(root, "source_message_id", out var sourceMessageId))
        {
            submission.SourceMessageId = sourceMessageId;
        }

        if (root.TryGetProperty("value", out var valueElement))
            CopyScalarMap(valueElement, submission.Arguments);
        if (root.TryGetProperty("form_value", out var formValueElement))
            CopyScalarMap(formValueElement, submission.FormFields);
        if (root.TryGetProperty("arguments", out var argumentsElement))
            CopyScalarMap(argumentsElement, submission.Arguments);
        if (root.TryGetProperty("form_fields", out var formFieldsElement))
            CopyScalarMap(formFieldsElement, submission.FormFields);

        if (string.IsNullOrEmpty(submission.ActionId) &&
            submission.Arguments.TryGetValue("agent_builder_action", out var builderAction) &&
            !string.IsNullOrWhiteSpace(builderAction))
        {
            submission.ActionId = builderAction;
        }

        return submission;
    }

    private static void CopyScalarMap(JsonElement element, Google.Protobuf.Collections.MapField<string, string> target)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    target[property.Name] = property.Value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    target[property.Name] = property.Value.ToString();
                    break;
            }
        }
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var parsed = property.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(parsed))
            return false;

        value = parsed;
        return true;
    }

    private static string NormalizePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? "unknown" : platform.Trim().ToLowerInvariant();

    private static string ResolvePlatformMessageId(NyxIdRelayCallbackPayload payload, string platform)
    {
        var directPlatformId = payload.PlatformMessageId?.Trim();
        if (!string.IsNullOrWhiteSpace(directPlatformId))
            return directPlatformId;

        if (payload.RawPlatformData is not { } rawPlatformData)
            return string.Empty;

        return platform switch
        {
            "lark" or "feishu" => ResolveLarkPlatformMessageId(rawPlatformData),
            _ => string.Empty,
        };
    }

    private static string ResolveLarkPlatformMessageId(JsonElement rawPlatformData)
    {
        if (TryReadJsonString(rawPlatformData, out var replyTarget, "event", "context", "open_message_id"))
            return replyTarget;

        if (TryReadJsonString(rawPlatformData, out var messageId, "event", "message", "message_id"))
            return messageId;

        return string.Empty;
    }

    private static bool TryReadJsonString(
        JsonElement element,
        out string value,
        params string[] path)
    {
        value = string.Empty;
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind != JsonValueKind.String)
            return false;

        var parsed = current.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(parsed))
            return false;

        value = parsed;
        return true;
    }

    private static string ResolveConversationIdentity(string platform, NyxIdRelayCallbackPayload payload)
    {
        var conversationId = payload.Conversation?.Id?.Trim();
        if (!string.IsNullOrWhiteSpace(conversationId))
            return conversationId;

        var platformId = payload.Conversation?.PlatformId?.Trim();
        if (!string.IsNullOrWhiteSpace(platformId))
            return platformId;

        var senderId = payload.Sender?.PlatformId?.Trim();
        if (!string.IsNullOrWhiteSpace(senderId))
            return senderId;

        return $"{platform}-conversation";
    }

    /// <summary>
    /// Extracts the Lark <c>union_id</c> (<c>on_*</c>) of the inbound sender from the relay's
    /// <c>raw_platform_data</c>. <c>union_id</c> is tenant-stable and cross-app safe — outbound
    /// senders running under a different Lark app than the relay-side ingress app must use this
    /// to avoid <c>open_id cross app</c> rejections from Lark. Returns empty when the platform
    /// is not Lark or the original event did not surface a <c>union_id</c> at the well-known
    /// path. The empty case is normal for non-Lark traffic and for misconfigured Lark apps that
    /// have not enabled <c>union_id</c> emission.
    /// </summary>
    private static string ExtractLarkUnionId(string platform, NyxIdRelayCallbackPayload payload, bool isCardAction)
    {
        if (!IsLark(platform) || payload.RawPlatformData is not { } raw || raw.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (!raw.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            return string.Empty;

        // Lark `im.message.receive_v1` puts sender ids under `event.sender.sender_id`. Card
        // submissions (`card.action.trigger`) put the operator's union_id directly under
        // `event.operator`, since there is no `sender_id` envelope on that event shape.
        if (isCardAction)
        {
            if (evt.TryGetProperty("operator", out var op) && op.ValueKind == JsonValueKind.Object)
                return ReadStringProperty(op, "union_id");
            return string.Empty;
        }

        if (!evt.TryGetProperty("sender", out var sender) || sender.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (!sender.TryGetProperty("sender_id", out var senderId) || senderId.ValueKind != JsonValueKind.Object)
            return string.Empty;

        return ReadStringProperty(senderId, "union_id");
    }

    /// <summary>
    /// Extracts the Lark <c>chat_id</c> (<c>oc_*</c>) of the inbound conversation from the
    /// relay's <c>raw_platform_data</c>. Cross-app safe within the tenant for groups/threads/
    /// channels — any app added to the chat can address it via <c>receive_id_type=chat_id</c>.
    /// For p2p DMs the chat_id is bot-specific (each Lark app has its own DM thread with the
    /// user) and not cross-app safe; downstream senders must prefer <see cref="ExtractLarkUnionId"/>
    /// for p2p targets. Returns empty when the platform is not Lark or the event did not carry
    /// a <c>chat_id</c> at the well-known path.
    /// </summary>
    private static string ExtractLarkChatId(string platform, NyxIdRelayCallbackPayload payload, bool isCardAction)
    {
        if (!IsLark(platform) || payload.RawPlatformData is not { } raw || raw.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (!raw.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (isCardAction)
        {
            if (evt.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Object)
                return ReadStringProperty(ctx, "open_chat_id");
            return string.Empty;
        }

        if (!evt.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return string.Empty;

        return ReadStringProperty(message, "chat_id");
    }

    private static bool IsLark(string platform) =>
        string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase);

    private static string ReadStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return string.Empty;

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string BuildCanonicalKey(
        string platform,
        ConversationScope scope,
        string conversationIdentity,
        string? senderId)
    {
        var segments = scope == ConversationScope.DirectMessage && !string.IsNullOrWhiteSpace(senderId)
            ? new[] { BuildScopeSegment(scope), senderId.Trim() }
            : new[] { BuildScopeSegment(scope), conversationIdentity };
        return ConversationReference.BuildCanonicalKey(ChannelId.From(platform), segments);
    }

    private static string BuildScopeSegment(ConversationScope scope) =>
        scope switch
        {
            ConversationScope.DirectMessage => "dm",
            ConversationScope.Group => "group",
            ConversationScope.Channel => "channel",
            ConversationScope.Thread => "thread",
            _ => "conversation",
        };

    private static DateTimeOffset ParseTimestamp(string? timestamp)
    {
        if (DateTimeOffset.TryParse(timestamp, out var parsed))
            return parsed;

        return DateTimeOffset.UtcNow;
    }

    private static string ComputeBodyHash(byte[] bodyBytes)
    {
        var hash = SHA256.HashData(bodyBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
