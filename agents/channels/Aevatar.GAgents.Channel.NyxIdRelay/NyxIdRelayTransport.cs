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

        var text = payload.Content?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return NyxIdRelayParseResult.IgnoredPayload(payload, "empty_text", "Relay payload does not contain text content.");

        var conversationType = payload.Conversation?.Type ?? payload.Conversation?.ConversationType;
        if (!NyxIdRelayConversationTypeMap.TryMap(conversationType, out var scope))
        {
            return NyxIdRelayParseResult.IgnoredPayload(
                payload,
                "unsupported_conversation_type",
                $"Relay conversation.type '{conversationType ?? "<empty>"}' is not supported by channel runtime.");
        }

        var platform = NormalizePlatform(payload.Platform);
        var conversationIdentity = ResolveConversationIdentity(platform, payload);
        var senderId = payload.Sender?.PlatformId?.Trim();
        var canonicalKey = BuildCanonicalKey(platform, scope, conversationIdentity, senderId);
        var partition = conversationIdentity;
        var timestamp = ParseTimestamp(payload.Timestamp);
        var botId = payload.Agent?.ApiKeyId?.Trim();
        var platformMessageId = ResolvePlatformMessageId(payload, platform);

        var activity = new ChatActivity
        {
            Id = payload.MessageId.Trim(),
            Type = ActivityType.Message,
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
            Content = new MessageContent
            {
                Text = text,
            },
            RawPayloadBlobRef = $"{platform}-raw:{ComputeBodyHash(bodyBytes)}",
            OutboundDelivery = new OutboundDeliveryContext
            {
                ReplyMessageId = payload.MessageId.Trim(),
                ReplyAccessToken = payload.ReplyToken?.Trim() ?? string.Empty,
            },
            TransportExtras = new TransportExtras
            {
                NyxMessageId = payload.MessageId.Trim(),
                NyxAgentApiKeyId = payload.Agent?.ApiKeyId?.Trim() ?? string.Empty,
                NyxPlatform = platform,
                NyxConversationId = payload.Conversation?.Id?.Trim() ?? conversationIdentity,
                NyxPlatformMessageId = platformMessageId,
            },
        };

        activity.Conversation.CanonicalKey = canonicalKey;
        return NyxIdRelayParseResult.Parsed(payload, activity);
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
