using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.AI.ToolProviders.Lark;

internal static class LarkProxyResponseParser
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, OutputOptions);

    public static bool TryParseError(string? response, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
        {
            error = "empty_lark_response";
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.True)
            {
                var status = TryReadInt(root, "status");
                var message = TryReadString(root, "message");
                var body = TryReadString(root, "body");
                error = $"nyx_proxy_error status={status?.ToString() ?? "unknown"}";
                if (!string.IsNullOrWhiteSpace(message))
                    error += $" message={message}";
                if (!string.IsNullOrWhiteSpace(body))
                    error += $" body={body}";
                return true;
            }

            var payloadRoot = ResolveDataRoot(root);
            if (root.TryGetProperty("code", out var codeProp) &&
                codeProp.ValueKind == JsonValueKind.Number &&
                codeProp.GetInt32() != 0)
            {
                error = $"lark_code={codeProp.GetInt32()}";
                var message = TryReadString(root, "msg") ?? TryReadString(payloadRoot, "msg");
                if (!string.IsNullOrWhiteSpace(message))
                    error += $" msg={message}";
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            error = "invalid_lark_response_json";
            return true;
        }
    }

    public static LarkSendResult ParseSendSuccess(string response)
    {
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        return new LarkSendResult(
            MessageId: TryReadString(data, "message_id"),
            ChatId: TryReadString(data, "chat_id"),
            CreateTime: TryReadString(data, "create_time"));
    }

    public static LarkMessageReactionResult ParseReactionCreateSuccess(string response)
    {
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        return ParseReactionItem(data);
    }

    public static LarkMessageReactionListResult ParseReactionListSuccess(string response)
    {
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        var items = new List<LarkMessageReactionResult>();

        if (data.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsProp.EnumerateArray())
                items.Add(ParseReactionItem(item));
        }

        return new LarkMessageReactionListResult(
            Items: items,
            HasMore: TryReadBool(data, "has_more") ?? false,
            PageToken: TryReadString(data, "page_token"));
    }

    public static LarkChatLookupResult ParseChatSearchSuccess(
        string response,
        string? query,
        bool exactMatchHint)
    {
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        var candidates = new List<LarkChatCandidate>();

        if (data.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsProp.EnumerateArray())
            {
                var meta = item.TryGetProperty("meta_data", out var metaProp) &&
                           metaProp.ValueKind == JsonValueKind.Object
                    ? metaProp
                    : item;
                var title = TryReadString(meta, "name");
                var exactNameMatch = !string.IsNullOrWhiteSpace(query) &&
                                     !string.IsNullOrWhiteSpace(title) &&
                                     string.Equals(title, query, StringComparison.OrdinalIgnoreCase);

                candidates.Add(new LarkChatCandidate(
                    ChatId: TryReadString(meta, "chat_id"),
                    Title: title,
                    ChatMode: TryReadString(meta, "chat_mode"),
                    ChatStatus: TryReadString(meta, "chat_status"),
                    Description: TryReadString(meta, "description"),
                    OwnerId: TryReadString(meta, "owner_id"),
                    External: TryReadBool(meta, "external"),
                    ExactNameMatch: exactNameMatch));
            }
        }

        var ordered = exactMatchHint
            ? candidates
                .OrderByDescending(candidate => candidate.ExactNameMatch)
                .ThenBy(candidate => candidate.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : candidates;

        return new LarkChatLookupResult(
            ordered,
            Total: TryReadInt(data, "total") ?? ordered.Count,
            HasMore: TryReadBool(data, "has_more") ?? false,
            PageToken: TryReadString(data, "page_token"));
    }

    public static LarkMessageSearchResult ParseMessageSearchSuccess(string response)
    {
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        var messageIds = new List<string>();

        if (data.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsProp.EnumerateArray())
            {
                var meta = item.TryGetProperty("meta_data", out var metaProp) &&
                           metaProp.ValueKind == JsonValueKind.Object
                    ? metaProp
                    : item;
                var messageId = TryReadString(meta, "message_id");
                if (!string.IsNullOrWhiteSpace(messageId))
                    messageIds.Add(messageId);
            }
        }

        return new LarkMessageSearchResult(
            MessageIds: messageIds,
            HasMore: TryReadBool(data, "has_more") ?? false,
            PageToken: TryReadString(data, "page_token"),
            Count: TryReadInt(data, "count") ?? messageIds.Count);
    }

    public static LarkMessageBatchGetResult ParseMessageBatchGetSuccess(string response)
    {
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        var messages = new List<LarkMessageSummary>();

        if (data.TryGetProperty("items", out var itemsProp) && itemsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsProp.EnumerateArray())
            {
                var mentions = new List<LarkMessageMention>();
                if (item.TryGetProperty("mentions", out var mentionsProp) && mentionsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mention in mentionsProp.EnumerateArray())
                    {
                        mentions.Add(new LarkMessageMention(
                            Id: TryReadMentionId(mention),
                            Key: TryReadString(mention, "key"),
                            Name: TryReadString(mention, "name")));
                    }
                }

                var sender = item.TryGetProperty("sender", out var senderProp) &&
                             senderProp.ValueKind == JsonValueKind.Object
                    ? senderProp
                    : default;
                var body = item.TryGetProperty("body", out var bodyProp) &&
                           bodyProp.ValueKind == JsonValueKind.Object
                    ? bodyProp
                    : default;
                var contentJson = TryReadString(body, "content");
                var messageType = TryReadString(item, "msg_type");

                messages.Add(new LarkMessageSummary(
                    MessageId: TryReadString(item, "message_id"),
                    MessageType: messageType,
                    Content: ExtractMessageContent(messageType, contentJson),
                    ContentJson: contentJson,
                    ChatId: TryReadString(item, "chat_id"),
                    CreateTime: TryReadString(item, "create_time"),
                    ThreadId: TryReadString(item, "thread_id"),
                    ReplyTo: TryReadString(item, "parent_id"),
                    Deleted: TryReadBool(item, "deleted") ?? false,
                    Updated: TryReadBool(item, "updated") ?? false,
                    SenderId: TryReadString(sender, "id"),
                    SenderName: TryReadString(sender, "name"),
                    SenderType: TryReadString(sender, "sender_type"),
                    Mentions: mentions));
            }
        }

        return new LarkMessageBatchGetResult(Messages: messages);
    }

    public static LarkSheetAppendResult ParseSheetAppendSuccess(string response)
    {
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        var updates = data.TryGetProperty("updates", out var updatesProp) &&
                      updatesProp.ValueKind == JsonValueKind.Object
            ? updatesProp
            : default;

        return new LarkSheetAppendResult(
            UpdatedRange: TryReadString(updates, "updatedRange") ?? TryReadString(updates, "updated_range"),
            TableRange: TryReadString(data, "tableRange") ?? TryReadString(data, "table_range"),
            UpdatedRows: TryReadInt(updates, "updatedRows") ?? TryReadInt(updates, "updated_rows"),
            UpdatedColumns: TryReadInt(updates, "updatedColumns") ?? TryReadInt(updates, "updated_columns"),
            UpdatedCells: TryReadInt(updates, "updatedCells") ?? TryReadInt(updates, "updated_cells"));
    }

    public static LarkApprovalTaskQueryResult ParseApprovalTaskQuerySuccess(string response)
    {
        using var document = JsonDocument.Parse(response);
        var data = ResolveDataRoot(document.RootElement);
        var tasks = new List<LarkApprovalTaskSummary>();

        if (data.TryGetProperty("tasks", out var tasksProp) && tasksProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var task in tasksProp.EnumerateArray())
            {
                var summaries = new List<LarkApprovalTaskField>();
                if (task.TryGetProperty("summaries", out var summariesProp) && summariesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var summary in summariesProp.EnumerateArray())
                    {
                        summaries.Add(new LarkApprovalTaskField(
                            Key: TryReadString(summary, "key"),
                            Value: TryReadString(summary, "value")));
                    }
                }

                tasks.Add(new LarkApprovalTaskSummary(
                    TaskId: TryReadString(task, "task_id"),
                    InstanceCode: TryReadString(task, "instance_code"),
                    Title: TryReadString(task, "title"),
                    Status: TryReadString(task, "status"),
                    Topic: TryReadString(task, "topic"),
                    SupportApiOperate: TryReadBool(task, "support_api_operate"),
                    DefinitionCode: TryReadString(task, "definition_code"),
                    DefinitionName: TryReadString(task, "definition_name"),
                    Initiator: TryReadString(task, "initiator"),
                    InitiatorName: TryReadString(task, "initiator_name"),
                    UserId: TryReadString(task, "user_id"),
                    InstanceStatus: TryReadString(task, "instance_status"),
                    Link: TryReadString(task, "link"),
                    Summaries: summaries));
            }
        }

        return new LarkApprovalTaskQueryResult(
            Tasks: tasks,
            Count: TryReadInt(data, "count") ?? tasks.Count,
            HasMore: TryReadBool(data, "has_more") ?? false,
            PageToken: TryReadString(data, "page_token"));
    }

    private static JsonElement ResolveDataRoot(JsonElement root) =>
        root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object
            ? dataProp
            : root;

    private static LarkMessageReactionResult ParseReactionItem(JsonElement item)
    {
        var operatorData = item.TryGetProperty("operator", out var operatorProp) &&
                           operatorProp.ValueKind == JsonValueKind.Object
            ? operatorProp
            : default;
        var reactionType = item.TryGetProperty("reaction_type", out var reactionProp) &&
                           reactionProp.ValueKind == JsonValueKind.Object
            ? reactionProp
            : default;

        return new LarkMessageReactionResult(
            ReactionId: TryReadString(item, "reaction_id"),
            OperatorId: TryReadString(operatorData, "operator_id"),
            OperatorType: TryReadString(operatorData, "operator_type"),
            ActionTime: TryReadString(item, "action_time"),
            EmojiType: TryReadString(reactionType, "emoji_type"));
    }

    private static string? ExtractMessageContent(string? messageType, string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(contentJson);
            var root = document.RootElement;
            if (messageType is "text" && root.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                return textProp.GetString();
            if (messageType is "file" && root.TryGetProperty("file_name", out var fileNameProp) && fileNameProp.ValueKind == JsonValueKind.String)
                return $"[file] {fileNameProp.GetString()}";
            if (messageType is "image")
                return "[image]";
            if (messageType is "audio")
                return "[audio]";
            if (messageType is "video" or "media")
                return "[video]";
            if (messageType is "interactive")
                return "[interactive_card]";
        }
        catch (JsonException)
        {
            // Keep the raw content below.
        }

        return contentJson;
    }

    private static string? TryReadMentionId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out var idProp))
            return null;

        if (idProp.ValueKind == JsonValueKind.String)
            return idProp.GetString();
        if (idProp.ValueKind == JsonValueKind.Object)
            return TryReadString(idProp, "open_id") ?? TryReadString(idProp, "user_id");
        return null;
    }

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryReadInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;

    private static bool? TryReadBool(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
}

internal sealed record LarkSendResult(
    string? MessageId,
    string? ChatId,
    string? CreateTime);

internal sealed record LarkMessageReactionResult(
    string? ReactionId,
    string? OperatorId,
    string? OperatorType,
    string? ActionTime,
    string? EmojiType);

internal sealed record LarkMessageReactionListResult(
    IReadOnlyList<LarkMessageReactionResult> Items,
    bool HasMore,
    string? PageToken);

internal sealed record LarkMessageSearchResult(
    IReadOnlyList<string> MessageIds,
    bool HasMore,
    string? PageToken,
    int Count);

internal sealed record LarkMessageMention(
    string? Id,
    string? Key,
    string? Name);

internal sealed record LarkMessageSummary(
    string? MessageId,
    string? MessageType,
    string? Content,
    string? ContentJson,
    string? ChatId,
    string? CreateTime,
    string? ThreadId,
    string? ReplyTo,
    bool Deleted,
    bool Updated,
    string? SenderId,
    string? SenderName,
    string? SenderType,
    IReadOnlyList<LarkMessageMention> Mentions);

internal sealed record LarkMessageBatchGetResult(
    IReadOnlyList<LarkMessageSummary> Messages);

internal sealed record LarkChatCandidate(
    string? ChatId,
    string? Title,
    string? ChatMode,
    string? ChatStatus,
    string? Description,
    string? OwnerId,
    bool? External,
    bool ExactNameMatch);

internal sealed record LarkChatLookupResult(
    IReadOnlyList<LarkChatCandidate> Chats,
    int Total,
    bool HasMore,
    string? PageToken);

internal sealed record LarkSheetAppendResult(
    string? UpdatedRange,
    string? TableRange,
    int? UpdatedRows,
    int? UpdatedColumns,
    int? UpdatedCells);

internal sealed record LarkApprovalTaskField(
    string? Key,
    string? Value);

internal sealed record LarkApprovalTaskSummary(
    string? TaskId,
    string? InstanceCode,
    string? Title,
    string? Status,
    string? Topic,
    bool? SupportApiOperate,
    string? DefinitionCode,
    string? DefinitionName,
    string? Initiator,
    string? InitiatorName,
    string? UserId,
    string? InstanceStatus,
    string? Link,
    IReadOnlyList<LarkApprovalTaskField> Summaries);

internal sealed record LarkApprovalTaskQueryResult(
    IReadOnlyList<LarkApprovalTaskSummary> Tasks,
    int Count,
    bool HasMore,
    string? PageToken);
