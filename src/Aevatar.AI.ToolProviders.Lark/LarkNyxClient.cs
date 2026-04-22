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
