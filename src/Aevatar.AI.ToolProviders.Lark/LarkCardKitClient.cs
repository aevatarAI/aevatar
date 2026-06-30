using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Platform.Lark.Abstractions;

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
        if (string.IsNullOrWhiteSpace(request.DataJson))
        {
            throw new ArgumentException(
                $"{nameof(request.DataJson)} must be non-empty.",
                nameof(request.DataJson));
        }

        // Lark CardKit 2.0 `data` shape depends on `type`:
        //   • card_json → a JSON-encoded STRING of the card schema (escape-serialized).
        //     Lark rejects an inline object here with code 9499 "Invalid parameter
        //     type in json: Data" (verified against open-apis/cardkit/v1/cards on
        //     2026-05-08; an empty stub plus type=card_json with inline object 400s,
        //     same body with `data` as a string returns 200 + a usable card_id).
        //   • card_id   → a STRING (the existing card_id we want to clone).
        //   • template  → an INLINE object: `{ template_id, template_variable }`.
        //
        // The earlier implementation always reparsed DataJson as an inline JsonNode,
        // which 400s on the card_json path and silently drove the production
        // CardKit-streaming default-on rollout into the legacy edit-message fallback
        // for every turn (PR #562 / PR #590 incident, 2026-05-08 06:51 UTC).
        var body = new Dictionary<string, object?>
        {
            ["type"] = request.Type,
            ["data"] = request.Type switch
            {
                "card_json" or "card_id" => request.DataJson,
                _ => ParseJsonObject(request.DataJson, nameof(request.DataJson), request.Type),
            },
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
        if (string.IsNullOrWhiteSpace(request.SettingsJson))
        {
            throw new ArgumentException(
                $"{nameof(request.SettingsJson)} must be non-empty.",
                nameof(request.SettingsJson));
        }

        // PATCH /cards/{id}/settings expects `settings` to be a JSON-encoded STRING (same
        // contract surprise as POST /cards's `data` field — verified against the live
        // endpoint on 2026-05-08, inline object 400s with code 9499 "Invalid parameter
        // type in json: Settings", same body with stringified settings returns 200).
        var body = new Dictionary<string, object?>
        {
            ["settings"] = request.SettingsJson,
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
        if (string.IsNullOrWhiteSpace(request.CardJson))
        {
            throw new ArgumentException(
                $"{nameof(request.CardJson)} must be non-empty.",
                nameof(request.CardJson));
        }
        // Validate the inner shape parses (we don't keep the parsed tree — Lark wants the
        // raw string per the contract below — but malformed JSON should fail here, not as
        // a 400 from Lark's parser). Surfaces JsonException to the caller.
        _ = JsonNode.Parse(request.CardJson)
            ?? throw new ArgumentException(
                $"{nameof(request.CardJson)} parsed to null.",
                nameof(request.CardJson));

        // PUT /cards/{id} replaces the card payload with a fresh `{type, data}` envelope
        // (Lark's full-card-replace contract — verified 2026-05-08: inline `card: {schema:..., body:...}`
        // 400s with `card.type is required`, same body wrapped as `card: {type: "card_json",
        // data: "<stringified-card-json>"}` returns 200). Caller passes the inner card body
        // as a JSON string in `CardJson`; we wrap it here so the call site stays simple.
        var body = new Dictionary<string, object?>
        {
            ["card"] = new Dictionary<string, object?>
            {
                ["type"] = "card_json",
                ["data"] = request.CardJson,
            },
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
    private static JsonNode? ParseJsonObject(string json, string paramName, string cardType)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException($"{paramName} must be non-empty JSON for card type '{cardType}'.", paramName);
        return JsonNode.Parse(json)
            ?? throw new ArgumentException($"{paramName} parsed to null for card type '{cardType}'.", paramName);
    }
}
