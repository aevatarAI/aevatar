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
        var platform = NormalizePlatform(payload.Platform);
        if (!isCardAction && string.IsNullOrWhiteSpace(text) && IsLark(platform))
        {
            // NyxID only normalizes Lark `text`/`image`/`file` messages; a rich-text `post`
            // (图文夹杂) arrives as content_type=unknown with no normalized text, so recover
            // the user's words from the raw post body or the turn is dropped as empty_text
            // and the bot never replies.
            text = NormalizeOptional(ExtractLarkRawText(payload));
        }
        var attachments = BuildAttachments(payload, platform);
        if (!isCardAction && string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
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

        var larkFacts = ResolveLarkRelayConversationFacts(platform, payload, isCardAction);

        var conversationType = payload.Conversation?.Type ?? payload.Conversation?.ConversationType;
        ConversationScope scope;
        if (larkFacts.Scope is { } resolvedLarkScope)
        {
            scope = resolvedLarkScope;
        }
        else if (!NyxIdRelayConversationTypeMap.TryMap(conversationType, out scope))
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

        var conversationIdentity = ResolveConversationIdentity(platform, payload);
        if (IsLark(platform) && !isCardAction && IsGroupLike(scope))
        {
            conversationIdentity = NormalizeOptional(larkFacts.GroupConversationIdentity)
                ?? NormalizeOptional(payload.Conversation?.PlatformId)
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(conversationIdentity))
            {
                return NyxIdRelayParseResult.IgnoredPayload(
                    payload,
                    "missing_lark_group_chat_identity",
                    "Lark group relay payload is missing raw event.message.chat_id and conversation.platform_id.");
            }
        }

        var senderId = payload.Sender?.PlatformId?.Trim();
        var canonicalKey = BuildCanonicalKey(platform, scope, conversationIdentity, senderId);
        var partition = conversationIdentity;
        var timestamp = ParseTimestamp(payload.Timestamp);
        var botId = payload.Agent?.ApiKeyId?.Trim();
        var platformMessageId = ResolvePlatformMessageId(payload, platform, larkFacts);
        var deliveryTarget = ResolveDeliveryTarget(platform, scope, conversationIdentity, senderId, larkFacts, isCardAction);
        var correlationId = string.IsNullOrWhiteSpace(payload.CorrelationId)
            ? payload.MessageId.Trim()
            : payload.CorrelationId.Trim();

        var content = new MessageContent
        {
            Text = isCardAction ? string.Empty : text ?? string.Empty,
        };
        content.Attachments.AddRange(attachments);
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
                NyxConversationId = NormalizeOptional(payload.Conversation?.Id) ?? conversationIdentity,
                NyxPlatformMessageId = platformMessageId,
                NyxLarkUnionId = NormalizeOptional(larkFacts.SenderUnionId)
                    ?? NormalizeOptional(larkFacts.OperatorUnionId)
                    ?? string.Empty,
                NyxLarkChatId = NormalizeOptional(larkFacts.ChatId) ?? string.Empty,
                NyxLarkOperatorUserId = NormalizeOptional(larkFacts.OperatorUserId) ?? string.Empty,
                NyxLarkOperatorOpenId = NormalizeOptional(larkFacts.OperatorOpenId) ?? string.Empty,
                NyxLarkOperatorUnionId = NormalizeOptional(larkFacts.OperatorUnionId) ?? string.Empty,
                DeliveryAddressId = deliveryTarget.PrimaryAddressId,
                DeliveryAddressType = deliveryTarget.PrimaryAddressType,
                DeliveryFallbackAddressId = deliveryTarget.FallbackAddressId,
                DeliveryFallbackAddressType = deliveryTarget.FallbackAddressType,
            },
        };

        // Reply target + @-mentions feed the group-chat admission gate (only engage when the
        // bot is mentioned, the message replies to one of the bot's messages, or it is a slash
        // command). NyxID forwards both verbatim but normalizes neither into typed fields.
        if (!isCardAction)
        {
            activity.ReplyToActivityId = NormalizeOptional(payload.ReplyToPlatformMessageId) ?? string.Empty;
            if (IsLark(platform))
                activity.Mentions.AddRange(BuildLarkMentions(payload));
        }

        activity.Conversation.CanonicalKey = canonicalKey;
        return NyxIdRelayParseResult.Parsed(payload, activity);
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
                var attachment = BuildAttachment(callbackAttachment);
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

    private static AttachmentRef? BuildAttachment(NyxIdRelayAttachmentPayload attachment)
    {
        var locator = NormalizeOptional(attachment.Url);
        if (locator is null)
            return null;

        var contentType = NormalizeOptional(attachment.MimeType)
            ?? NormalizeOptional(attachment.ContentType)
            ?? NormalizeOptional(attachment.Type)
            ?? string.Empty;
        var kind = MapAttachmentKind(attachment.ContentType ?? attachment.Type ?? attachment.MimeType);
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
            ExternalUrl = IsHttpUrl(locator) ? locator : string.Empty,
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

        if (TryReadString(root, "action_id", out var actionId) ||
            TryReadString(root, "a", out actionId))
        {
            submission.ActionId = actionId;
        }

        if (TryReadString(root, "submitted_value", out var submittedValue) ||
            TryReadString(root, "s", out submittedValue))
        {
            submission.SubmittedValue = submittedValue;
        }

        if (string.IsNullOrEmpty(submission.SourceMessageId) &&
            TryReadString(root, "source_message_id", out var sourceMessageId))
        {
            submission.SourceMessageId = sourceMessageId;
        }

        if (root.TryGetProperty("value", out var valueElement))
            CopyScalarMap(valueElement, submission.Arguments);
        if (root.TryGetProperty("v", out var compactValueElement))
            CopyScalarMap(compactValueElement, submission.Arguments);
        if (root.TryGetProperty("form_value", out var formValueElement))
            CopyScalarMap(formValueElement, submission.FormFields);
        if (root.TryGetProperty("arguments", out var argumentsElement))
            CopyScalarMap(argumentsElement, submission.Arguments);
        if (root.TryGetProperty("form_fields", out var formFieldsElement))
            CopyScalarMap(formFieldsElement, submission.FormFields);

        submission.ActionKind = ResolveActionKind(root, submission.Arguments);
        submission.Arguments.Remove("action_kind");

        // Lark relays button clicks with the composed value object nested under `value`
        // (`{"tag":"button","value":{"action_id":...,"value":...},...}`), so the typed
        // identity fields arrive flattened into Arguments rather than at the root. Mirror
        // them into the typed fields without removing the boundary arguments: workflow
        // resume and other typed callback consumers still rely on the original payload.
        if (string.IsNullOrEmpty(submission.ActionId) &&
            submission.Arguments.TryGetValue("action_id", out var nestedActionId) &&
            !string.IsNullOrWhiteSpace(nestedActionId))
        {
            submission.ActionId = nestedActionId.Trim();
        }

        if (string.IsNullOrEmpty(submission.SubmittedValue) &&
            submission.Arguments.TryGetValue("value", out var nestedValue) &&
            !string.IsNullOrWhiteSpace(nestedValue))
        {
            submission.SubmittedValue = nestedValue;
        }

        MapKnownPayloads(submission);

        if (string.IsNullOrEmpty(submission.ActionId) &&
            submission.Arguments.TryGetValue("agent_builder_action", out var builderAction) &&
            !string.IsNullOrWhiteSpace(builderAction))
        {
            submission.ActionId = builderAction;
        }

        return submission;
    }

    private static void MapKnownPayloads(CardActionSubmission submission)
    {
        // Refactor (iter93/cluster-093):
        // Old: workflow resume + LLM selection control semantics lived in the open `arguments` map.
        // New: repository-owned semantics use typed payloads; `arguments` is only for adapter/third-party
        // extension data plus legacy callback JSON inbound compatibility.
        if (TryBuildWorkflowResumePayload(submission, out var workflowResume))
        {
            submission.WorkflowResume = workflowResume;
            RemoveKeys(
                submission.Arguments,
                "actor_id",
                "run_id",
                "step_id",
                "approved",
                "execution_id",
                "tool_call_id",
                "approval_request_id");
        }

        if (TryBuildLlmSelectionPayload(submission, out var llmSelection))
        {
            submission.LlmSelection = llmSelection;
            RemoveKeys(
                submission.Arguments,
                "llm_action",
                "service_id",
                "preset_id",
                "model",
                "page",
                "display_mode");
        }

        if (TryBuildNyxIdApprovalPayload(submission, out var nyxIdApproval))
        {
            submission.NyxIdApproval = nyxIdApproval;
            RemoveKeys(
                submission.Arguments,
                "nyxid_approval_request_id",
                "nyxid_approval_approved");
        }
    }

    private static ActionElementKind ResolveActionKind(
        JsonElement root,
        Google.Protobuf.Collections.MapField<string, string> arguments)
    {
        if (arguments.TryGetValue("action_kind", out var actionKind) &&
            TryMapActionKind(actionKind, out var mappedFromValue))
        {
            return mappedFromValue;
        }

        if (TryReadString(root, "action_kind", out var rootActionKind) &&
            TryMapActionKind(rootActionKind, out var mappedFromRoot))
        {
            return mappedFromRoot;
        }

        if (TryReadString(root, "tag", out var tag) &&
            TryMapLarkActionTag(tag, out var mappedFromTag))
        {
            return mappedFromTag;
        }

        return ActionElementKind.Unspecified;
    }

    private static bool TryMapActionKind(string? value, out ActionElementKind kind)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        kind = normalized switch
        {
            "button" => ActionElementKind.Button,
            "select" or "select_static" => ActionElementKind.Select,
            "text_input" or "input" => ActionElementKind.TextInput,
            "form_submit" or "submit" => ActionElementKind.FormSubmit,
            "link" => ActionElementKind.Link,
            _ => ActionElementKind.Unspecified,
        };
        return kind != ActionElementKind.Unspecified;
    }

    private static bool TryMapLarkActionTag(string? value, out ActionElementKind kind)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        kind = normalized switch
        {
            "button" => ActionElementKind.Button,
            "select_static" => ActionElementKind.Select,
            "input" => ActionElementKind.TextInput,
            _ => ActionElementKind.Unspecified,
        };
        return kind != ActionElementKind.Unspecified;
    }

    private static bool TryBuildWorkflowResumePayload(
        CardActionSubmission submission,
        out WorkflowResumeActionPayload payload)
    {
        payload = new WorkflowResumeActionPayload();
        if (!TryGetRequiredValue(submission.Arguments, "actor_id", out var actorId) ||
            !TryGetRequiredValue(submission.Arguments, "run_id", out var runId) ||
            !TryGetRequiredValue(submission.Arguments, "step_id", out var stepId))
        {
            return false;
        }

        payload.ActorId = actorId;
        payload.RunId = runId;
        payload.StepId = stepId;
        if (submission.Arguments.TryGetValue("approved", out var rawApproved) &&
            bool.TryParse(rawApproved, out var approved))
        {
            payload.Approved = approved;
        }

        if (submission.FormFields.TryGetValue("user_input", out var userInput))
            payload.UserInput = userInput ?? string.Empty;
        if (submission.FormFields.TryGetValue("edited_content", out var editedContent))
            payload.EditedContent = editedContent ?? string.Empty;
        if (submission.FormFields.TryGetValue("feedback", out var feedback))
            payload.Feedback = feedback ?? string.Empty;

        if (TryBuildWorkflowToolApprovalResumePayload(submission, out var toolApproval))
            payload.ToolApproval = toolApproval;

        return true;
    }

    private static bool TryBuildWorkflowToolApprovalResumePayload(
        CardActionSubmission submission,
        out WorkflowToolApprovalResumeActionPayload payload)
    {
        payload = new WorkflowToolApprovalResumeActionPayload();
        if (!TryGetRequiredValue(submission.Arguments, "execution_id", out var executionId) ||
            !TryGetRequiredValue(submission.Arguments, "tool_call_id", out var toolCallId) ||
            !TryGetRequiredValue(submission.Arguments, "approval_request_id", out var approvalRequestId))
        {
            return false;
        }

        payload.ExecutionId = executionId;
        payload.ToolCallId = toolCallId;
        payload.ApprovalRequestId = approvalRequestId;
        return true;
    }

    private static bool TryBuildLlmSelectionPayload(
        CardActionSubmission submission,
        out LlmSelectionActionPayload payload)
    {
        payload = new LlmSelectionActionPayload();

        if (!submission.Arguments.TryGetValue("llm_action", out var rawAction) ||
            string.IsNullOrWhiteSpace(rawAction))
        {
            rawAction = submission.ActionId switch
            {
                "ls" or "llm_select_service" => "select_service",
                "lp" or "llm_apply_preset" => "apply_preset",
                _ => string.Empty,
            };
        }

        if (string.IsNullOrWhiteSpace(rawAction))
            return false;

        payload.Action = rawAction.Trim();
        if (submission.Arguments.TryGetValue("service_id", out var serviceId) &&
            !string.IsNullOrWhiteSpace(serviceId))
        {
            payload.ServiceId = serviceId.Trim();
        }
        else if (payload.Action == "select_service" && !string.IsNullOrWhiteSpace(submission.SubmittedValue))
        {
            payload.ServiceId = submission.SubmittedValue.Trim();
        }

        if (submission.Arguments.TryGetValue("preset_id", out var presetId) &&
            !string.IsNullOrWhiteSpace(presetId))
        {
            payload.PresetId = presetId.Trim();
        }
        else if (payload.Action == "apply_preset" && !string.IsNullOrWhiteSpace(submission.SubmittedValue))
        {
            payload.PresetId = submission.SubmittedValue.Trim();
        }

        if (submission.Arguments.TryGetValue("model", out var model) &&
            !string.IsNullOrWhiteSpace(model))
        {
            payload.Model = model.Trim();
        }

        if (submission.Arguments.TryGetValue("page", out var rawPage) &&
            int.TryParse(rawPage, out var page) &&
            page > 0)
        {
            payload.Page = page;
        }
        else if (payload.Action == "list_page" &&
                 !string.IsNullOrWhiteSpace(submission.SubmittedValue) &&
                 int.TryParse(submission.SubmittedValue, out var submittedPage) &&
                 submittedPage > 0)
        {
            payload.Page = submittedPage;
        }

        if (submission.Arguments.TryGetValue("display_mode", out var displayMode) &&
            !string.IsNullOrWhiteSpace(displayMode))
        {
            payload.DisplayMode = displayMode.Trim();
        }

        return true;
    }

    private static bool TryBuildNyxIdApprovalPayload(
        CardActionSubmission submission,
        out NyxIdApprovalActionPayload payload)
    {
        payload = new NyxIdApprovalActionPayload();
        if (!TryGetRequiredValue(submission.Arguments, "nyxid_approval_request_id", out var requestId))
            return false;

        if (!submission.Arguments.TryGetValue("nyxid_approval_approved", out var rawApproved) ||
            !bool.TryParse(rawApproved, out var approved))
        {
            return false;
        }

        payload.RequestId = requestId;
        payload.Approved = approved;
        return true;
    }

    private static bool TryGetRequiredValue(
        Google.Protobuf.Collections.MapField<string, string> values,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(key, out var raw))
            return false;

        value = (raw ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void RemoveKeys(
        Google.Protobuf.Collections.MapField<string, string> values,
        params string[] keys)
    {
        foreach (var key in keys)
            values.Remove(key);
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
            "lark" or "feishu" => NormalizeOptional(larkFacts.PlatformMessageId) ?? string.Empty,
            _ => string.Empty,
        };
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
