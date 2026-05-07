using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkCardKitClient : ILarkCardKitClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LarkToolOptions _options;
    private readonly NyxIdApiClient _nyxClient;

    public LarkCardKitClient(LarkToolOptions options, NyxIdApiClient nyxClient)
    {
        _options = options;
        _nyxClient = nyxClient;
    }

    public Task<string> CreateCardAsync(string token, LarkCardKitCreateRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = request.Type,
            ["data"] = ParseJsonObject(request.DataJson, nameof(request.DataJson)),
        };

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            "open-apis/cardkit/v1/cards",
            "POST",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> StreamElementContentAsync(string token, LarkCardKitStreamElementContentRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["content"] = request.Content,
            ["sequence"] = request.Sequence,
        };
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            body["uuid"] = request.IdempotencyKey.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/cardkit/v1/cards/{Uri.EscapeDataString(request.CardId)}/elements/{Uri.EscapeDataString(request.ElementId)}/content",
            "PUT",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> SetCardSettingsAsync(string token, LarkCardKitSettingsRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["settings"] = ParseJsonObject(request.SettingsJson, nameof(request.SettingsJson)),
            ["sequence"] = request.Sequence,
        };
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            body["uuid"] = request.IdempotencyKey.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/cardkit/v1/cards/{Uri.EscapeDataString(request.CardId)}/settings",
            "PATCH",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> UpdateCardAsync(string token, LarkCardKitUpdateRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["card"] = ParseJsonObject(request.CardJson, nameof(request.CardJson)),
            ["sequence"] = request.Sequence,
        };
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            body["uuid"] = request.IdempotencyKey.Trim();

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            $"open-apis/cardkit/v1/cards/{Uri.EscapeDataString(request.CardId)}",
            "PUT",
            JsonSerializer.Serialize(body, JsonOptions),
            extraHeaders: null,
            ct);
    }

    /// <summary>
    /// Lark CardKit accepts inline objects for <c>data</c>/<c>settings</c>/<c>card</c>; we
    /// take a JSON string from the caller (typed DTOs in the streaming sink) and re-embed
    /// as a <see cref="JsonNode"/> so System.Text.Json serializes it in line rather than
    /// double-encoding as a string.
    /// </summary>
    private static JsonNode? ParseJsonObject(string json, string paramName)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException($"{paramName} must be non-empty JSON.", paramName);
        return JsonNode.Parse(json)
            ?? throw new ArgumentException($"{paramName} parsed to null.", paramName);
    }
}
