using System.Globalization;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

public sealed class LarkMessagesSearchTool : AgentToolBase<LarkMessagesSearchTool.Parameters>
{
    private static readonly HashSet<string> AllowedAttachmentTypes =
    [
        "file",
        "image",
        "video",
        "link",
    ];

    private static readonly HashSet<string> AllowedChatTypes =
    [
        "group",
        "p2p",
    ];

    private static readonly HashSet<string> AllowedSenderTypes =
    [
        "user",
        "bot",
    ];

    private readonly ILarkNyxClient _client;

    public LarkMessagesSearchTool(ILarkNyxClient client)
    {
        _client = client;
    }

    public override string Name => "lark_messages_search";

    public override string Description =>
        "Search Lark messages with filters such as keyword, sender, chat, and time range. " +
        "By default this also hydrates the current page into full message details.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;
    public override bool IsReadOnly => true;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var query = parameters.Query?.Trim() ?? string.Empty;
        var chatIds = parameters.ChatIds?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var senderIds = parameters.SenderIds?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var includeAttachmentType = parameters.IncludeAttachmentType?.Trim().ToLowerInvariant();
        var chatType = parameters.ChatType?.Trim().ToLowerInvariant();
        var senderType = parameters.SenderType?.Trim().ToLowerInvariant();
        var excludeSenderType = parameters.ExcludeSenderType?.Trim().ToLowerInvariant();
        var startTime = parameters.StartTime?.Trim();
        var endTime = parameters.EndTime?.Trim();

        if (string.IsNullOrWhiteSpace(query) &&
            (chatIds is null || chatIds.Length == 0) &&
            (senderIds is null || senderIds.Length == 0) &&
            string.IsNullOrWhiteSpace(includeAttachmentType) &&
            string.IsNullOrWhiteSpace(chatType) &&
            string.IsNullOrWhiteSpace(senderType) &&
            string.IsNullOrWhiteSpace(excludeSenderType) &&
            string.IsNullOrWhiteSpace(startTime) &&
            string.IsNullOrWhiteSpace(endTime) &&
            parameters.IsAtMe != true)
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "At least one search filter is required (query, chat_ids, sender_ids, attachment type, sender/chat type, time range, or is_at_me).",
            });
        }

        if (!string.IsNullOrWhiteSpace(includeAttachmentType) && !AllowedAttachmentTypes.Contains(includeAttachmentType))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "include_attachment_type must be one of: file, image, video, link" });
        if (!string.IsNullOrWhiteSpace(chatType) && !AllowedChatTypes.Contains(chatType))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "chat_type must be one of: group, p2p" });
        if (!string.IsNullOrWhiteSpace(senderType) && !AllowedSenderTypes.Contains(senderType))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "sender_type must be one of: user, bot" });
        if (!string.IsNullOrWhiteSpace(excludeSenderType) && !AllowedSenderTypes.Contains(excludeSenderType))
            return LarkProxyResponseParser.Serialize(new { success = false, error = "exclude_sender_type must be one of: user, bot" });
        if (!string.IsNullOrWhiteSpace(senderType) &&
            string.Equals(senderType, excludeSenderType, StringComparison.OrdinalIgnoreCase))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = false,
                error = "sender_type and exclude_sender_type cannot be the same value.",
            });
        }

        if (!TryParseIsoTime(startTime, out var startAt, out var startError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = startError });
        if (!TryParseIsoTime(endTime, out var endAt, out var endError))
            return LarkProxyResponseParser.Serialize(new { success = false, error = endError });
        if (startAt.HasValue && endAt.HasValue && startAt > endAt)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "start_time cannot be later than end_time." });

        var pageSize = parameters.PageSize is > 0 ? parameters.PageSize.Value : 20;
        if (pageSize is < 1 or > 50)
            return LarkProxyResponseParser.Serialize(new { success = false, error = "page_size must be between 1 and 50." });

        var response = await _client.SearchMessagesAsync(
            token,
            new LarkMessageSearchRequest(
                Query: query,
                ChatIds: chatIds,
                SenderIds: senderIds,
                IncludeAttachmentType: includeAttachmentType,
                ChatType: chatType,
                SenderType: senderType,
                ExcludeSenderType: excludeSenderType,
                IsAtMe: parameters.IsAtMe ?? false,
                StartTime: startTime,
                EndTime: endTime,
                PageSize: pageSize,
                PageToken: parameters.PageToken?.Trim()),
            ct);

        if (LarkProxyResponseParser.TryParseError(response, out var error))
            return LarkProxyResponseParser.Serialize(new { success = false, error });

        var searchResult = LarkProxyResponseParser.ParseMessageSearchSuccess(response);
        if (searchResult.MessageIds.Count == 0)
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = true,
                total = 0,
                has_more = searchResult.HasMore,
                page_token = searchResult.PageToken,
                message_ids = Array.Empty<string>(),
                messages = Array.Empty<object>(),
            });
        }

        var hydrate = parameters.Hydrate is not false;
        if (!hydrate)
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = true,
                total = searchResult.MessageIds.Count,
                has_more = searchResult.HasMore,
                page_token = searchResult.PageToken,
                message_ids = searchResult.MessageIds.ToArray(),
            });
        }

        var detailsResponse = await _client.BatchGetMessagesAsync(
            token,
            new LarkMessagesBatchGetRequest(searchResult.MessageIds),
            ct);

        if (LarkProxyResponseParser.TryParseError(detailsResponse, out var detailsError))
        {
            return LarkProxyResponseParser.Serialize(new
            {
                success = true,
                total = searchResult.MessageIds.Count,
                has_more = searchResult.HasMore,
                page_token = searchResult.PageToken,
                message_ids = searchResult.MessageIds.ToArray(),
                warning = $"message hydration failed: {detailsError}",
            });
        }

        var details = LarkProxyResponseParser.ParseMessageBatchGetSuccess(detailsResponse);
        return LarkProxyResponseParser.Serialize(new
        {
            success = true,
            total = details.Messages.Count,
            has_more = searchResult.HasMore,
            page_token = searchResult.PageToken,
            message_ids = searchResult.MessageIds.ToArray(),
            messages = details.Messages.Select(message => new
            {
                message_id = message.MessageId,
                msg_type = message.MessageType,
                content = message.Content,
                content_json = message.ContentJson,
                chat_id = message.ChatId,
                create_time = message.CreateTime,
                thread_id = message.ThreadId,
                reply_to = message.ReplyTo,
                deleted = message.Deleted,
                updated = message.Updated,
                sender = new
                {
                    id = message.SenderId,
                    name = message.SenderName,
                    sender_type = message.SenderType,
                },
                mentions = message.Mentions.Select(mention => new
                {
                    id = mention.Id,
                    key = mention.Key,
                    name = mention.Name,
                }).ToArray(),
            }).ToArray(),
        });
    }

    private static bool TryParseIsoTime(string? raw, out DateTimeOffset? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            error = "start_time and end_time must be ISO 8601 timestamps with timezone offsets.";
            return false;
        }

        value = parsed;
        return true;
    }

    public sealed class Parameters
    {
        public string? Query { get; set; }
        public List<string>? ChatIds { get; set; }
        public List<string>? SenderIds { get; set; }
        public string? IncludeAttachmentType { get; set; }
        public string? ChatType { get; set; }
        public string? SenderType { get; set; }
        public string? ExcludeSenderType { get; set; }
        public bool? IsAtMe { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public int? PageSize { get; set; }
        public string? PageToken { get; set; }
        public bool? Hydrate { get; set; }
    }
}
