using System.Security.Cryptography;
using System.Text.Json;
using Aevatar.Foundation.Abstractions;
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
    private readonly IReadOnlyList<INyxIdRelayContentAdapter> _contentAdapters;
    private readonly IReadOnlyList<INyxIdRelayConversationAdapter> _conversationAdapters;
    private readonly IReadOnlyList<INyxIdRelayMentionAdapter> _mentionAdapters;
    private readonly IReadOnlyList<INyxIdRelayInteractionAdapter> _interactionAdapters;

    public NyxIdRelayTransport(
        IEnumerable<INyxIdRelayContentAdapter>? contentAdapters = null,
        IEnumerable<INyxIdRelayConversationAdapter>? conversationAdapters = null,
        IEnumerable<INyxIdRelayMentionAdapter>? mentionAdapters = null,
        IEnumerable<INyxIdRelayInteractionAdapter>? interactionAdapters = null)
    {
        _contentAdapters = ChannelPlatformBehavior.Validate(contentAdapters);
        _conversationAdapters = ChannelPlatformBehavior.Validate(conversationAdapters);
        _mentionAdapters = ChannelPlatformBehavior.Validate(mentionAdapters);
        _interactionAdapters = ChannelPlatformBehavior.Validate(interactionAdapters);
    }

    /// <summary>Read the external envelope before authentication, without invoking platform behavior.</summary>
    public static NyxIdRelayCallbackPayload? ReadPayload(byte[] bodyBytes)
    {
        try { return JsonSerializer.Deserialize<NyxIdRelayCallbackPayload>(bodyBytes, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public NyxIdRelayParseResult Parse(byte[] bodyBytes)
    {
        ArgumentNullException.ThrowIfNull(bodyBytes);
        NyxIdRelayCallbackPayload? payload;
        try { payload = JsonSerializer.Deserialize<NyxIdRelayCallbackPayload>(bodyBytes, JsonOptions); }
        catch (JsonException) { return NyxIdRelayParseResult.Invalid("invalid_relay_payload", "Failed to parse relay payload."); }
        if (payload is null)
            return NyxIdRelayParseResult.Invalid("missing_relay_payload", "Relay payload is required.");
        if (string.IsNullOrWhiteSpace(payload.MessageId))
            return NyxIdRelayParseResult.Invalid("missing_message_id", "Relay payload is missing message_id.");
        string platform;
        try { platform = ChannelPlatformId.ParseExternal(payload.Platform).Value; }
        catch (ArgumentException) { return NyxIdRelayParseResult.Invalid("invalid_platform", "Relay platform must be a valid canonical identity."); }

        var contentType = (NormalizeOptional(payload.Content?.ContentType) ?? NormalizeOptional(payload.Content?.Type) ?? string.Empty).ToLowerInvariant();
        var isInteraction = contentType == "card_action";
        var contentAdapter = ChannelPlatformBehavior.Resolve(_contentAdapters, platform);
        var interactionAdapter = ChannelPlatformBehavior.Resolve(_interactionAdapters, platform);
        MessageContent content;
        if (isInteraction && interactionAdapter is not null)
        {
            var action = interactionAdapter.NormalizeInteraction(ReadPayload(bodyBytes)!);
            if (action is null)
                return NyxIdRelayParseResult.IgnoredPayload(payload, "invalid_card_action_payload", "Card action must contain a valid typed submission.");
            content = new MessageContent { CardAction = action };
        }
        else if (!isInteraction && contentAdapter is not null && contentType is not ("typing" or "reaction" or "edit"))
        {
            var relayPayload = ReadPayload(bodyBytes)!;
            if (!contentAdapter.SupportsContent(relayPayload))
                return NyxIdRelayParseResult.IgnoredPayload(payload, "unsupported_content_type", "The platform has no behavior for this content type.");
            content = contentAdapter.NormalizeContent(relayPayload);
        }
        else
        {
            if (contentType is not ("" or "text") || payload.Content?.Attachments is { Count: > 0 })
                return NyxIdRelayParseResult.IgnoredPayload(payload, "unsupported_content_type", "The platform has no behavior for this content type.");
            content = new MessageContent { Text = payload.Content?.Text?.Trim() ?? string.Empty };
        }
        if (!isInteraction && string.IsNullOrWhiteSpace(content.Text) && content.Attachments.Count == 0)
            return NyxIdRelayParseResult.IgnoredPayload(payload, "empty_text", "Relay payload does not contain text content.");

        var mapped = NyxIdRelayConversationTypeMap.TryMap(payload.Conversation?.Type ?? payload.Conversation?.ConversationType, out var scope);
        if (!mapped && !isInteraction)
            return NyxIdRelayParseResult.IgnoredPayload(payload, "unsupported_conversation_type", "Relay conversation.type is not supported.");
        var identity = NormalizeOptional(payload.Conversation?.Id) ?? NormalizeOptional(payload.Conversation?.PlatformId)
            ?? NormalizeOptional(payload.Sender?.PlatformId) ?? $"{platform}-conversation";
        var conversationAdapter = ChannelPlatformBehavior.Resolve(_conversationAdapters, platform);
        var extras = new TransportExtras();
        if (conversationAdapter is not null)
        {
            var normalized = conversationAdapter.NormalizeConversation(ReadPayload(bodyBytes)!, scope, identity, isInteraction);
            if (normalized.ErrorCode is not null)
                return NyxIdRelayParseResult.IgnoredPayload(payload, normalized.ErrorCode, "The platform conversation address is unavailable.");
            scope = normalized.Scope;
            identity = normalized.ConversationIdentity;
            extras = normalized.DeliveryFacts;
        }
        if (scope == ConversationScope.Unspecified && !isInteraction)
            return NyxIdRelayParseResult.IgnoredPayload(payload, "unsupported_conversation_type", "Relay conversation.type is not supported.");
        // Raw protocol adapters may refine a recognized route scope, but may never turn an unknown scope into private.
        if (!mapped && scope == ConversationScope.DirectMessage)
            return NyxIdRelayParseResult.IgnoredPayload(payload, "unsupported_conversation_type", "Unknown conversation.type cannot become private.");

        var senderId = payload.Sender?.PlatformId?.Trim() ?? string.Empty;
        var botId = NormalizeOptional(payload.Agent?.ApiKeyId) ?? "nyx-relay-bot";
        var scopeSegment = scope switch
        {
            ConversationScope.DirectMessage => "dm", ConversationScope.Group => "group",
            ConversationScope.Channel => "channel", ConversationScope.Thread => "thread", _ => "conversation",
        };
        var correlation = NormalizeOptional(payload.CorrelationId) ?? payload.MessageId.Trim();
        extras.NyxMessageId = payload.MessageId.Trim();
        extras.NyxAgentApiKeyId = payload.Agent?.ApiKeyId?.Trim() ?? string.Empty;
        extras.NyxPlatform = platform;
        extras.NyxConversationId = NormalizeOptional(payload.Conversation?.Id) ?? identity;
        if (string.IsNullOrEmpty(extras.NyxPlatformMessageId))
            extras.NyxPlatformMessageId = NormalizeOptional(payload.PlatformMessageId) ?? string.Empty;
        var activity = new ChatActivity
        {
            Id = payload.MessageId.Trim(), Type = isInteraction ? ActivityType.CardAction : ActivityType.Message,
            ChannelId = ChannelId.From(platform), Bot = BotInstanceId.From(botId),
            Conversation = ConversationReference.Create(ChannelId.From(platform), BotInstanceId.From(botId), scope,
                identity, scopeSegment, scope == ConversationScope.DirectMessage && senderId.Length > 0 ? senderId : identity),
            From = new ParticipantRef { CanonicalId = senderId, DisplayName = payload.Sender?.DisplayName?.Trim() ?? string.Empty },
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.TryParse(payload.Timestamp, out var timestamp) ? timestamp : DateTimeOffset.UtcNow),
            Content = content,
            RawPayloadBlobRef = $"{platform}-raw:{Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant()}",
            OutboundDelivery = new OutboundDeliveryContext { ReplyMessageId = payload.MessageId.Trim(), CorrelationId = correlation },
            TransportExtras = extras,
        };
        if (!isInteraction)
        {
            activity.ReplyToActivityId = NormalizeOptional(payload.ReplyToPlatformMessageId) ?? string.Empty;
            var mentions = ChannelPlatformBehavior.Resolve(_mentionAdapters, platform);
            if (mentions is not null)
                activity.Mentions.AddRange(mentions.NormalizeMentions(ReadPayload(bodyBytes)!));
        }
        return NyxIdRelayParseResult.Parsed(payload, activity);
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
