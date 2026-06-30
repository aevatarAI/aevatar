namespace Aevatar.GAgents.Platform.Lark.Abstractions;

/// <summary>
/// Wrapper over Lark CardKit 2.0 REST endpoints (<c>/open-apis/cardkit/v1/...</c>) routed
/// through the NyxID API-key proxy.
/// </summary>
/// <remarks>
/// All methods return raw JSON response bodies; the caller is responsible for parsing. Required
/// scopes on the Lark bot app: <c>cardkit:card:read</c> and <c>cardkit:card:write</c>.
/// </remarks>
public interface ILarkCardKitClient
{
    /// <summary>
    /// Creates a new card entity. Returns raw JSON containing <c>card_id</c> at
    /// <c>data.card_id</c>; the caller extracts it before subsequent updates. Endpoint:
    /// <c>POST /open-apis/cardkit/v1/cards</c>.
    /// </summary>
    Task<string> CreateCardAsync(string token, LarkCardKitCreateRequest request, CancellationToken ct);

    /// <summary>
    /// Streams text into a single card element with typewriter rendering on the client. Updates
    /// are ordered by <see cref="LarkCardKitStreamElementContentRequest.Sequence"/>; stale
    /// sequences are rejected by Lark deterministically. Endpoint:
    /// <c>PUT /open-apis/cardkit/v1/cards/{card_id}/elements/{element_id}/content</c>.
    /// </summary>
    Task<string> StreamElementContentAsync(string token, LarkCardKitStreamElementContentRequest request, CancellationToken ct);

    /// <summary>
    /// Toggles card-level settings. Endpoint:
    /// <c>PATCH /open-apis/cardkit/v1/cards/{card_id}/settings</c>.
    /// </summary>
    Task<string> SetCardSettingsAsync(string token, LarkCardKitSettingsRequest request, CancellationToken ct);

    /// <summary>
    /// Replaces the full card content. Endpoint: <c>PUT /open-apis/cardkit/v1/cards/{card_id}</c>.
    /// </summary>
    Task<string> UpdateCardAsync(string token, LarkCardKitUpdateRequest request, CancellationToken ct);
}

/// <param name="Type">
/// Card source type. <c>card_json</c> for an inline card definition; <c>template</c> for a
/// stored Lark template id reference.
/// </param>
/// <param name="DataJson">
/// JSON-serialized card payload. For <c>card_json</c>, the inline card schema; for
/// <c>template</c>, the template id and bound variables.
/// </param>
public sealed record LarkCardKitCreateRequest(string Type, string DataJson);

/// <param name="CardId">Card entity id returned by <c>CreateCardAsync</c>.</param>
/// <param name="ElementId">Element id within the card to stream into.</param>
/// <param name="Content">Latest accumulated text to render into the element.</param>
/// <param name="Sequence">Monotonically increasing sequence number for ordering.</param>
/// <param name="IdempotencyKey">Optional <c>uuid</c> for safe retry under network loss.</param>
public sealed record LarkCardKitStreamElementContentRequest(
    string CardId,
    string ElementId,
    string Content,
    long Sequence,
    string? IdempotencyKey = null);

/// <param name="SettingsJson">JSON-serialized settings patch.</param>
public sealed record LarkCardKitSettingsRequest(
    string CardId,
    string SettingsJson,
    long Sequence,
    string? IdempotencyKey = null);

/// <param name="CardJson">JSON-serialized full card replacement.</param>
public sealed record LarkCardKitUpdateRequest(
    string CardId,
    string CardJson,
    long Sequence,
    string? IdempotencyKey = null);
