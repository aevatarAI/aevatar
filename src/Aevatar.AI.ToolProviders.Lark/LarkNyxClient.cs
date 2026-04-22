using System.Text.Json;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkNyxClient : ILarkNyxClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LarkToolOptions _options;
    private readonly NyxIdApiClient _nyxClient;

    public LarkNyxClient(LarkToolOptions options, NyxIdApiClient nyxClient)
    {
        _options = options;
        _nyxClient = nyxClient;
    }

    public Task<string> SendMessageAsync(string token, LarkSendMessageRequest request, CancellationToken ct)
    {
        var path =
            $"open-apis/im/v1/messages?receive_id_type={Uri.EscapeDataString(request.TargetType)}";
        var body = new Dictionary<string, object?>
        {
            ["receive_id"] = request.TargetId,
            ["msg_type"] = request.MessageType,
            ["content"] = request.ContentJson,
        };

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            body["uuid"] = request.IdempotencyKey.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            path,
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> SearchChatsAsync(string token, LarkChatSearchRequest request, CancellationToken ct)
    {
        var query = $"page_size={request.PageSize}";
        if (!string.IsNullOrWhiteSpace(request.PageToken))
            query += $"&page_token={Uri.EscapeDataString(request.PageToken.Trim())}";

        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(request.Query))
            body["query"] = NormalizeChatSearchQuery(request.Query.Trim());

        var filter = new Dictionary<string, object?>();
        if (request.SearchTypes is { Count: > 0 })
            filter["search_types"] = request.SearchTypes;
        if (request.MemberIds is { Count: > 0 })
            filter["member_ids"] = request.MemberIds;
        if (request.IsManager)
            filter["is_manager"] = true;
        if (request.DisableSearchByUser)
            filter["disable_search_by_user"] = true;
        if (filter.Count > 0)
            body["filter"] = filter;

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/im/v2/chats/search?{query}",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> AppendSheetRowsAsync(string token, LarkSheetAppendRowsRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["valueRange"] = new Dictionary<string, object?>
            {
                ["range"] = request.Range,
                ["values"] = request.Rows,
            },
        };

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/sheets/v2/spreadsheets/{Uri.EscapeDataString(request.SpreadsheetToken)}/values_append",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> ListApprovalTasksAsync(string token, LarkApprovalTaskQueryRequest request, CancellationToken ct)
    {
        var queryParts = new List<string>
        {
            $"topic={Uri.EscapeDataString(request.Topic)}",
            $"page_size={request.PageSize}",
        };
        if (!string.IsNullOrWhiteSpace(request.DefinitionCode))
            queryParts.Add($"definition_code={Uri.EscapeDataString(request.DefinitionCode.Trim())}");
        if (!string.IsNullOrWhiteSpace(request.Locale))
            queryParts.Add($"locale={Uri.EscapeDataString(request.Locale.Trim())}");
        if (!string.IsNullOrWhiteSpace(request.PageToken))
            queryParts.Add($"page_token={Uri.EscapeDataString(request.PageToken.Trim())}");
        if (!string.IsNullOrWhiteSpace(request.UserIdType))
            queryParts.Add($"user_id_type={Uri.EscapeDataString(request.UserIdType.Trim())}");

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/approval/v4/tasks?{string.Join("&", queryParts)}",
            "GET",
            body: null,
            extraHeaders: null,
            ct);
    }

    public Task<string> ActOnApprovalTaskAsync(string token, LarkApprovalTaskActionRequest request, CancellationToken ct)
    {
        var path = request.Action switch
        {
            "approve" => "open-apis/approval/v4/tasks/pass",
            "reject" => "open-apis/approval/v4/tasks/refuse",
            "transfer" => BuildTransferPath(request.UserIdType),
            _ => throw new InvalidOperationException($"Unsupported approval action: {request.Action}"),
        };

        var body = new Dictionary<string, object?>
        {
            ["instance_code"] = request.InstanceCode,
            ["task_id"] = request.TaskId,
        };

        if (!string.IsNullOrWhiteSpace(request.Comment))
            body["comment"] = request.Comment.Trim();
        if (!string.IsNullOrWhiteSpace(request.FormJson))
            body["form"] = request.FormJson.Trim();
        if (!string.IsNullOrWhiteSpace(request.TransferUserId))
            body["transfer_user_id"] = request.TransferUserId.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            path,
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    private static string BuildTransferPath(string? userIdType)
    {
        if (string.IsNullOrWhiteSpace(userIdType))
            return "open-apis/approval/v4/tasks/forward";
        return $"open-apis/approval/v4/tasks/forward?user_id_type={Uri.EscapeDataString(userIdType.Trim())}";
    }

    internal static string NormalizeChatSearchQuery(string query)
    {
        if (!query.Contains('-', StringComparison.Ordinal))
            return query;

        try
        {
            query = JsonSerializer.Deserialize<string>($"\"{query.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")
                ?? query;
        }
        catch (JsonException)
        {
            // Keep the original query if unquoting fails; the outer quoting below is the only required behavior.
        }

        return JsonSerializer.Serialize(query);
    }
}
