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
        var larkFacts = ResolveLarkRelayConversationFacts(platform, payload, isCardAction);
        var platformMessageId = ResolvePlatformMessageId(payload, platform, larkFacts);
        var attachments = NyxIdRelayAttachmentNormalizer.Normalize(payload, platform, platformMessageId);
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
        var correlationId = string.IsNullOrWhiteSpace(payload.CorrelationId)
            ? payload.MessageId.Trim()
            : payload.CorrelationId.Trim();

        var content = new MessageContent
        {
            Text = isCardAction ? string.Empty : text ?? string.Empty,
        };
        if (cardAction is not null)
            content.CardAction = cardAction;
        content.Attachments.AddRange(attachments);

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
            },
        };

        activity.Conversation.CanonicalKey = canonicalKey;
        return NyxIdRelayParseResult.Parsed(payload, activity);
    }

    private const string CardActionContentType = "card_action";

    private readonly record struct LarkRelayConversationFacts(
        ConversationScope? Scope,
        string? GroupConversationIdentity,
        string? ChatId,
        string? PlatformMessageId,
        string? SenderUnionId,
        string? OperatorUserId,
        string? OperatorOpenId,
        string? OperatorUnionId);

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
                "approved");
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
