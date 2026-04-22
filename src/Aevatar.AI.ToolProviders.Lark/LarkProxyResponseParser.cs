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

    private static JsonElement ResolveDataRoot(JsonElement root) =>
        root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object
            ? dataProp
            : root;

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;

    private static bool? TryReadBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
}

internal sealed record LarkSendResult(
    string? MessageId,
    string? ChatId,
    string? CreateTime);

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
