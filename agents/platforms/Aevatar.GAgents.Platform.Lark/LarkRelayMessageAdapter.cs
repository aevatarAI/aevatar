using System.Text;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>Lark protocol normalization only; never authenticates or changes verified identities.</summary>
public class LarkRelayMessageAdapter : INyxIdRelayContentAdapter, INyxIdRelayConversationAdapter,
    INyxIdRelayMentionAdapter, INyxIdRelayInteractionAdapter
{
    public virtual string Platform => "lark";

    public bool SupportsContent(NyxIdRelayCallbackPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var typed = NormalizeContentType(payload.Content);
        if (typed is "text" or "post" or "rich_text" or "image" or "file" or "audio" or "media" or "sticker")
            return true;

        // Only unknown or omitted legacy message types can use a raw-message snapshot.
        // An explicit unsupported operation must not become a new message through its snapshot.
        if (typed is not ("" or "unknown"))
            return false;

        // NyxID forwards Lark post messages as content_type=unknown. Preserve that
        // fallback only when the nested shape identifies a supported Lark message type.
        if (!TryGetLarkRawMessage(payload, out var rawMessage) ||
            !rawMessage.TryGetProperty("message_type", out var messageType) ||
            messageType.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var rawType = messageType.GetString()?.Trim().ToLowerInvariant();
        return rawType is "text" or "post" or "image" or "file" or "audio" or "media" or "sticker";
    }

    public MessageContent NormalizeContent(NyxIdRelayCallbackPayload payload)
    {
        var content = new MessageContent
        {
            Text = NormalizeOptional(payload.Content?.Text) ?? NormalizeOptional(ExtractLarkRawText(payload)) ?? string.Empty,
        };
        content.Attachments.AddRange(BuildAttachments(payload, Platform));
        return content;
    }

    public CardActionSubmission? NormalizeInteraction(NyxIdRelayCallbackPayload payload) =>
        NyxIdRelayCardActionParser.Parse(payload.Content?.Text?.Trim(), payload);

    public List<ParticipantRef> NormalizeMentions(NyxIdRelayCallbackPayload payload) => BuildLarkMentions(payload);

    public NyxIdRelayConversationNormalization NormalizeConversation(
        NyxIdRelayCallbackPayload payload, ConversationScope mappedScope, string conversationIdentity, bool isInteraction)
    {
        var facts = ResolveLarkRelayConversationFacts(Platform, payload, isInteraction);
        var scope = facts.Scope ?? mappedScope;
        if (!isInteraction && IsGroupLike(scope))
        {
            conversationIdentity = NormalizeOptional(facts.GroupConversationIdentity)
                ?? NormalizeOptional(payload.Conversation?.PlatformId) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(conversationIdentity))
                return new(scope, conversationIdentity, new TransportExtras(), "missing_lark_group_chat_identity");
        }
        var delivery = ResolveDeliveryTarget(Platform, scope, conversationIdentity,
            payload.Sender?.PlatformId?.Trim(), facts, isInteraction);
        return new(scope, conversationIdentity, new TransportExtras
        {
            NyxPlatformMessageId = ResolvePlatformMessageId(payload, Platform, facts),
            NyxLarkUnionId = NormalizeOptional(facts.SenderUnionId) ?? NormalizeOptional(facts.OperatorUnionId) ?? string.Empty,
            NyxLarkChatId = NormalizeOptional(facts.ChatId) ?? string.Empty,
            NyxLarkOperatorUserId = NormalizeOptional(facts.OperatorUserId) ?? string.Empty,
            NyxLarkOperatorOpenId = NormalizeOptional(facts.OperatorOpenId) ?? string.Empty,
            NyxLarkOperatorUnionId = NormalizeOptional(facts.OperatorUnionId) ?? string.Empty,
            DeliveryAddressId = delivery.PrimaryAddressId,
            DeliveryAddressType = delivery.PrimaryAddressType,
            DeliveryFallbackAddressId = delivery.FallbackAddressId,
            DeliveryFallbackAddressType = delivery.FallbackAddressType,
        });
    }

    private const string CardActionContentType = "card_action";

    private sealed record LarkAttachmentCandidate(
        string Key,
        AttachmentKind Kind,
        string ContentType,
        string? Name,
        long? SizeBytes);

    private readonly record struct LarkRelayConversationFacts(
        ConversationScope? Scope,
        string? GroupConversationIdentity,
        string? ChatId,
        string? PlatformMessageId,
        string? SenderUnionId,
        string? OperatorUserId,
        string? OperatorOpenId,
        string? OperatorUnionId);

    private readonly record struct RelayDeliveryTarget(
        string PrimaryAddressId,
        string PrimaryAddressType,
        string FallbackAddressId,
        string FallbackAddressType);

    private readonly record struct RelayDeliveryAddress(string AddressId, string AddressType);

    private static string NormalizeContentType(NyxIdRelayContentPayload? content)
    {
        var value = content?.ContentType;
        if (string.IsNullOrWhiteSpace(value))
            value = content?.Type;
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static List<AttachmentRef> BuildAttachments(NyxIdRelayCallbackPayload payload, string platform)
    {
        var attachments = new List<AttachmentRef>();
        var seenAttachmentKeys = new HashSet<string>(StringComparer.Ordinal);

        if (payload.Content?.Attachments is { Count: > 0 } callbackAttachments)
        {
            foreach (var callbackAttachment in callbackAttachments)
            {
                var attachment = BuildAttachment(callbackAttachment, platform);
                if (attachment is not null)
                    AddAttachment(attachments, seenAttachmentKeys, attachment, platform);
            }
        }

        if (IsLark(platform))
        {
            foreach (var candidate in EnumerateLarkRawAttachmentCandidates(payload))
                AddAttachment(attachments, seenAttachmentKeys, BuildLarkAttachment(candidate), platform);
        }

        return attachments;
    }

    private static void AddAttachment(
        List<AttachmentRef> attachments,
        HashSet<string> seenAttachmentKeys,
        AttachmentRef attachment,
        string platform)
    {
        var dedupeKey = BuildAttachmentDedupeKey(attachment, platform);
        if (seenAttachmentKeys.Add(dedupeKey))
            attachments.Add(attachment);
    }

    private static string BuildAttachmentDedupeKey(AttachmentRef attachment, string platform)
    {
        var attachmentId = IsLark(platform)
            ? LarkAttachmentResourceKeys.Normalize(attachment.AttachmentId) ?? attachment.AttachmentId
            : attachment.AttachmentId;
        return $"{attachment.Kind}:{attachmentId}";
    }

    private static AttachmentRef? BuildAttachment(NyxIdRelayAttachmentPayload attachment, string platform)
    {
        var url = NormalizeOptional(attachment.Url);
        var imageKey = IsLark(platform) ? NormalizeOptional(attachment.ImageKey) : null;
        var fileKey = IsLark(platform) ? NormalizeOptional(attachment.FileKey) : null;
        var locator = imageKey ?? fileKey ?? url;
        if (locator is null)
            return null;

        var contentType = NormalizeOptional(attachment.MimeType)
            ?? NormalizeOptional(attachment.ContentType)
            ?? NormalizeOptional(attachment.Type)
            ?? string.Empty;
        var kind = imageKey is not null
            ? AttachmentKind.Image
            : fileKey is not null
                ? AttachmentKind.File
                : MapAttachmentKind(attachment.ContentType ?? attachment.Type ?? attachment.MimeType);
        var name = NormalizeOptional(attachment.Filename)
            ?? NormalizeOptional(attachment.FileName)
            ?? NormalizeOptional(attachment.Name)
            ?? string.Empty;

        return new AttachmentRef
        {
            AttachmentId = locator,
            Kind = kind,
            Name = name,
            ContentType = contentType,
            ExternalUrl = url is not null && IsHttpUrl(url) ? url : string.Empty,
            SizeBytes = NormalizeSizeBytes(attachment.SizeBytes),
        };
    }

    private static AttachmentRef BuildLarkAttachment(LarkAttachmentCandidate candidate)
    {
        return new AttachmentRef
        {
            AttachmentId = candidate.Key,
            Kind = candidate.Kind,
            Name = candidate.Name ?? string.Empty,
            ContentType = candidate.ContentType,
            SizeBytes = NormalizeSizeBytes(candidate.SizeBytes),
        };
    }

    private static IEnumerable<LarkAttachmentCandidate> EnumerateLarkRawAttachmentCandidates(
        NyxIdRelayCallbackPayload payload)
    {
        if (!TryGetLarkRawMessageContent(payload, out var content))
            yield break;

        foreach (var candidate in EnumerateLarkAttachmentCandidates(content))
            yield return candidate;
    }

    private static bool TryGetLarkRawMessageContent(NyxIdRelayCallbackPayload payload, out JsonElement content)
    {
        content = default;
        if (payload.RawPlatformData is not { } raw || raw.ValueKind != JsonValueKind.Object)
            return false;

        if (!raw.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            return false;

        if (!evt.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return false;

        return TryReadLarkMessageContentObject(message, out content);
    }

    private static bool TryGetLarkRawMessage(NyxIdRelayCallbackPayload payload, out JsonElement message)
    {
        message = default;
        if (payload.RawPlatformData is not { } raw || raw.ValueKind != JsonValueKind.Object)
            return false;

        if (!raw.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            return false;

        if (!evt.TryGetProperty("message", out var rawMessage) || rawMessage.ValueKind != JsonValueKind.Object)
            return false;

        message = rawMessage;
        return true;
    }

    // Lift Lark's event.message.mentions[] into typed ParticipantRefs. Each mention identifies a
    // mentioned party by open_id (preferred), union_id, or user_id; the runtime gate matches the
    // bot's own open_id against these to decide whether a group message addressed the bot.
    private static List<ParticipantRef> BuildLarkMentions(NyxIdRelayCallbackPayload payload)
    {
        var mentions = new List<ParticipantRef>();
        if (!TryGetLarkRawMessage(payload, out var message))
            return mentions;

        if (!message.TryGetProperty("mentions", out var rawMentions) ||
            rawMentions.ValueKind != JsonValueKind.Array)
        {
            return mentions;
        }

        foreach (var rawMention in rawMentions.EnumerateArray())
        {
            if (rawMention.ValueKind != JsonValueKind.Object)
                continue;

            var canonicalId = ResolveLarkMentionCanonicalId(rawMention);
            if (canonicalId is null)
                continue;

            mentions.Add(new ParticipantRef
            {
                CanonicalId = canonicalId,
                DisplayName = ReadOptionalStringProperty(rawMention, "name") ?? string.Empty,
            });
        }

        return mentions;
    }

    private static string? ResolveLarkMentionCanonicalId(JsonElement mention)
    {
        // Match on open_id only: it is always present on a Lark mention and is the same identity
        // space as the bot's own open_id (resolved from bot/v3/info), so it is the one value the
        // admission gate can compare against. union_id/user_id would never match and are ignored.
        if (!mention.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Object)
            return null;

        return ReadOptionalStringProperty(id, "open_id");
    }

    private static string? ExtractLarkRawText(NyxIdRelayCallbackPayload payload) =>
        TryGetLarkRawMessageContent(payload, out var content)
            ? ExtractLarkPostText(content)
            : null;

    private static bool TryReadLarkMessageContentObject(JsonElement message, out JsonElement content)
    {
        content = default;
        if (!message.TryGetProperty("content", out var contentProperty))
            return false;

        if (contentProperty.ValueKind == JsonValueKind.Object)
        {
            content = contentProperty.Clone();
            return true;
        }

        if (contentProperty.ValueKind != JsonValueKind.String)
            return false;

        var rawContent = contentProperty.GetString();
        if (string.IsNullOrWhiteSpace(rawContent))
            return false;

        try
        {
            using var document = JsonDocument.Parse(rawContent);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            content = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<LarkAttachmentCandidate> EnumerateLarkAttachmentCandidates(JsonElement content)
    {
        // Plain image/file messages carry the identifier at the content root.
        if (TryReadString(content, "image_key", out var imageKey))
        {
            yield return new LarkAttachmentCandidate(
                imageKey,
                AttachmentKind.Image,
                "image",
                ReadOptionalStringProperty(content, "file_name")
                    ?? ReadOptionalStringProperty(content, "name"),
                ReadOptionalInt64Property(content, "file_size"));
        }

        if (TryReadString(content, "file_key", out var fileKey))
        {
            yield return new LarkAttachmentCandidate(
                fileKey,
                AttachmentKind.File,
                ReadOptionalStringProperty(content, "mime_type") ?? "file",
                ReadOptionalStringProperty(content, "file_name")
                    ?? ReadOptionalStringProperty(content, "name"),
                ReadOptionalInt64Property(content, "file_size"));
        }

        // Rich-text (post / 富文本): images and media are nested inside the 2D `content`
        // paragraph array, so the root-level checks above never see them.
        foreach (var segment in EnumerateLarkPostSegments(content))
        {
            if (!TryReadString(segment, "tag", out var tag))
                continue;

            if (string.Equals(tag, "img", StringComparison.OrdinalIgnoreCase) &&
                TryReadString(segment, "image_key", out var postImageKey))
            {
                yield return new LarkAttachmentCandidate(
                    postImageKey,
                    AttachmentKind.Image,
                    "image",
                    ReadOptionalStringProperty(segment, "file_name")
                        ?? ReadOptionalStringProperty(segment, "name"),
                    ReadOptionalInt64Property(segment, "file_size"));
            }
            else if (string.Equals(tag, "media", StringComparison.OrdinalIgnoreCase) &&
                     TryReadString(segment, "file_key", out var postFileKey))
            {
                yield return new LarkAttachmentCandidate(
                    postFileKey,
                    AttachmentKind.Video,
                    ReadOptionalStringProperty(segment, "mime_type") ?? "video",
                    ReadOptionalStringProperty(segment, "file_name")
                        ?? ReadOptionalStringProperty(segment, "name"),
                    ReadOptionalInt64Property(segment, "file_size"));
            }
        }
    }

    // ─── Lark rich-text (post) extraction ───
    //
    // A Lark `post` message (the shape produced by mixing text + inline images, 图文夹杂)
    // nests both text runs and media inside a 2D `content` array:
    //   { "title": "..", "content": [ [ {"tag":"text","text":".."}, {"tag":"img","image_key":".."} ], .. ] }
    // NyxID forwards this verbatim under raw_platform_data but cannot normalize it, so the
    // transport recovers text and attachments here. Handles both the flat receive form and a
    // locale-wrapped ({ "zh_cn": { "content": [..] } }) form.

    private static IEnumerable<JsonElement> EnumerateLarkPostSegments(JsonElement content)
    {
        if (!TryGetLarkPostParagraphs(content, out var paragraphs))
            yield break;

        foreach (var paragraph in paragraphs.EnumerateArray())
        {
            if (paragraph.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var segment in paragraph.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Object)
                    yield return segment;
            }
        }
    }

    private static bool TryGetLarkPostParagraphs(JsonElement content, out JsonElement paragraphs)
    {
        paragraphs = default;
        if (content.ValueKind != JsonValueKind.Object)
            return false;

        // Flat receive form: { "title": "..", "content": [[..],[..]] }.
        if (content.TryGetProperty("content", out var direct) && direct.ValueKind == JsonValueKind.Array)
        {
            paragraphs = direct;
            return true;
        }

        // Locale-wrapped form: { "zh_cn": { "content": [[..]] }, "en_us": {..} }.
        foreach (var property in content.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                property.Value.TryGetProperty("content", out var nested) &&
                nested.ValueKind == JsonValueKind.Array)
            {
                paragraphs = nested;
                return true;
            }
        }

        return false;
    }

    private static string? ExtractLarkPostText(JsonElement content)
    {
        if (!TryGetLarkPostParagraphs(content, out var paragraphs))
            return null;

        var lines = new List<string>();
        foreach (var paragraph in paragraphs.EnumerateArray())
        {
            if (paragraph.ValueKind != JsonValueKind.Array)
                continue;

            var builder = new StringBuilder();
            foreach (var segment in paragraph.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Object)
                    AppendLarkPostSegmentText(segment, builder);
            }

            var line = builder.ToString().Trim();
            if (line.Length > 0)
                lines.Add(line);
        }

        // Lark posts carry an optional title; surface it as the leading line so the model
        // sees the same heading the user typed.
        var title = ReadLarkPostTitle(content);
        if (title is not null)
            lines.Insert(0, title);

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string? ReadLarkPostTitle(JsonElement content)
    {
        var title = ReadOptionalStringProperty(content, "title");
        if (title is not null)
            return title;

        if (content.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in content.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("content", out var nested) ||
                nested.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            title = ReadOptionalStringProperty(property.Value, "title");
            if (title is not null)
                return title;
        }

        return null;
    }

    private static void AppendLarkPostSegmentText(JsonElement segment, StringBuilder builder)
    {
        if (!TryReadString(segment, "tag", out var tag))
            return;

        switch (tag.ToLowerInvariant())
        {
            case "text":
            case "a":
                // Preserve internal whitespace so adjacent runs don't get glued together.
                if (TryReadRawString(segment, "text", out var text))
                    builder.Append(text);
                break;
            case "at":
                var name = ReadOptionalStringProperty(segment, "user_name");
                builder.Append(name is null ? "@" : $"@{name}");
                break;
        }
    }

    private static bool TryReadRawString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static AttachmentKind MapAttachmentKind(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToLowerInvariant() ?? string.Empty;
        if (normalized.StartsWith("image/", StringComparison.Ordinal) || normalized is "image" or "photo")
            return AttachmentKind.Image;
        if (normalized.StartsWith("audio/", StringComparison.Ordinal) || normalized is "audio" or "voice")
            return AttachmentKind.Audio;
        if (normalized.StartsWith("video/", StringComparison.Ordinal) || normalized is "video")
            return AttachmentKind.Video;
        if (normalized.StartsWith("text/html", StringComparison.Ordinal) || normalized is "link" or "url")
            return AttachmentKind.Link;
        return AttachmentKind.File;
    }

    private static long NormalizeSizeBytes(long? sizeBytes) =>
        sizeBytes is > 0 ? sizeBytes.Value : 0;

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool TryReadString(JsonElement root, string propertyName, out string value)
    {
        value = ReadStringProperty(root, propertyName);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string ResolvePlatformMessageId(
        NyxIdRelayCallbackPayload payload,
        string platform,
        LarkRelayConversationFacts larkFacts)
    {
        var directPlatformId = payload.PlatformMessageId?.Trim();
        if (!string.IsNullOrWhiteSpace(directPlatformId))
            return directPlatformId;

        return platform switch
        {
            "lark" or "feishu" => ResolveAttachmentPlatformMessageId(payload)
                                  ?? NormalizeOptional(larkFacts.PlatformMessageId)
                                  ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static string? ResolveAttachmentPlatformMessageId(NyxIdRelayCallbackPayload payload)
    {
        if (payload.Content?.Attachments is not { Count: > 0 } attachments)
            return null;

        foreach (var attachment in attachments)
        {
            if (NormalizeOptional(attachment.PlatformMessageId) is { } platformMessageId)
                return platformMessageId;
        }

        return null;
    }

    private static RelayDeliveryTarget ResolveDeliveryTarget(
        string platform,
        ConversationScope scope,
        string conversationIdentity,
        string? senderId,
        LarkRelayConversationFacts larkFacts,
        bool isCardAction)
    {
        if (!IsLark(platform))
            return EmptyDeliveryTarget();

        var chatType = isCardAction ? "card_action" : ResolveChatType(scope);
        var unionId = NormalizeOptional(larkFacts.SenderUnionId) ?? NormalizeOptional(larkFacts.OperatorUnionId);
        var primary = ResolvePrimaryLarkDeliveryAddress(chatType, conversationIdentity, senderId, unionId, larkFacts.ChatId);
        var fallback = ResolveFallbackLarkDeliveryAddress(chatType, primary.AddressType, unionId);
        return new RelayDeliveryTarget(
            primary.AddressId,
            primary.AddressType,
            fallback.AddressId,
            fallback.AddressType);
    }

    private static RelayDeliveryTarget EmptyDeliveryTarget() =>
        new(string.Empty, string.Empty, string.Empty, string.Empty);

    private static RelayDeliveryAddress ResolvePrimaryLarkDeliveryAddress(
        string chatType,
        string conversationIdentity,
        string? senderId,
        string? unionId,
        string? chatId)
    {
        var normalizedChatId = NormalizeOptional(chatId);
        if (normalizedChatId is not null)
            return new RelayDeliveryAddress(normalizedChatId, "chat_id");

        if (string.Equals(chatType, "p2p", StringComparison.Ordinal))
        {
            if (unionId is not null)
                return new RelayDeliveryAddress(unionId, "union_id");

            var normalizedSenderId = NormalizeOptional(senderId);
            if (normalizedSenderId is not null)
                return new RelayDeliveryAddress(normalizedSenderId, "open_id");

            return new RelayDeliveryAddress(string.Empty, string.Empty);
        }

        return new RelayDeliveryAddress(
            NormalizeOptional(conversationIdentity) ?? string.Empty,
            "chat_id");
    }

    private static RelayDeliveryAddress ResolveFallbackLarkDeliveryAddress(
        string chatType,
        string primaryAddressType,
        string? unionId)
    {
        if (!string.Equals(chatType, "p2p", StringComparison.Ordinal) ||
            !string.Equals(primaryAddressType, "chat_id", StringComparison.Ordinal) ||
            unionId is null)
        {
            return new RelayDeliveryAddress(string.Empty, string.Empty);
        }

        return new RelayDeliveryAddress(unionId, "union_id");
    }

    private static LarkRelayConversationFacts ResolveLarkRelayConversationFacts(
        string platform,
        NyxIdRelayCallbackPayload payload,
        bool isCardAction)
    {
        if (!IsLark(platform) || payload.RawPlatformData is not { } raw || raw.ValueKind != JsonValueKind.Object)
            return default;

        if (!raw.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
            return default;

        if (isCardAction)
        {
            var cardChatId = string.Empty;
            var cardPlatformMessageId = string.Empty;
            if (evt.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Object)
            {
                cardChatId = ReadStringProperty(ctx, "open_chat_id");
                cardPlatformMessageId = ReadStringProperty(ctx, "open_message_id");
            }

            var operatorUserId = string.Empty;
            var operatorOpenId = string.Empty;
            var operatorUnionId = string.Empty;
            if (evt.TryGetProperty("operator", out var op) && op.ValueKind == JsonValueKind.Object)
            {
                operatorUserId = ReadStringProperty(op, "user_id");
                operatorOpenId = ReadStringProperty(op, "open_id");
                operatorUnionId = ReadStringProperty(op, "union_id");
            }

            return new LarkRelayConversationFacts(
                Scope: null,
                GroupConversationIdentity: null,
                ChatId: cardChatId,
                PlatformMessageId: cardPlatformMessageId,
                SenderUnionId: null,
                OperatorUserId: operatorUserId,
                OperatorOpenId: operatorOpenId,
                OperatorUnionId: operatorUnionId);
        }

        var chatId = string.Empty;
        var platformMessageId = string.Empty;
        ConversationScope? scope = null;
        if (evt.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            chatId = ReadStringProperty(message, "chat_id");
            platformMessageId = ReadStringProperty(message, "message_id");
            scope = MapLarkChatType(ReadStringProperty(message, "chat_type"));
        }

        var senderUnionId = string.Empty;
        if (evt.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object &&
            sender.TryGetProperty("sender_id", out var senderId) && senderId.ValueKind == JsonValueKind.Object)
        {
            senderUnionId = ReadStringProperty(senderId, "union_id");
        }

        return new LarkRelayConversationFacts(
            Scope: scope,
            GroupConversationIdentity: chatId,
            ChatId: chatId,
            PlatformMessageId: platformMessageId,
            SenderUnionId: senderUnionId,
            OperatorUserId: null,
            OperatorOpenId: null,
            OperatorUnionId: null);
    }

    private static bool IsLark(string platform) =>
        string.Equals(platform, "lark", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platform, "feishu", StringComparison.OrdinalIgnoreCase);

    private static bool IsGroupLike(ConversationScope scope) =>
        scope is ConversationScope.Group
              or ConversationScope.Channel
              or ConversationScope.Thread;

    private static ConversationScope? MapLarkChatType(string? chatType)
    {
        var normalized = NormalizeOptional(chatType)?.ToLowerInvariant();
        return normalized switch
        {
            "p2p" or "private" or "dm" => ConversationScope.DirectMessage,
            "group" => ConversationScope.Group,
            "topic" or "thread" => ConversationScope.Thread,
            "channel" => ConversationScope.Channel,
            _ => null,
        };
    }

    private static string ResolveChatType(ConversationScope scope) =>
        scope switch
        {
            ConversationScope.DirectMessage => "p2p",
            ConversationScope.Group => "group",
            ConversationScope.Channel => "channel",
            ConversationScope.Thread => "thread",
            _ => "conversation",
        };

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string ReadStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return string.Empty;

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string? ReadOptionalStringProperty(JsonElement element, string propertyName)
    {
        var value = ReadStringProperty(element, propertyName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long? ReadOptionalInt64Property(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            return number;

        if (property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString()?.Trim(), out var parsed))
        {
            return parsed;
        }

        return null;
    }


}

/// <summary>Explicit existing Feishu boundary; canonical identity stays feishu.</summary>
public sealed class FeishuRelayMessageAdapter : LarkRelayMessageAdapter
{
    public override string Platform => "feishu";
}
