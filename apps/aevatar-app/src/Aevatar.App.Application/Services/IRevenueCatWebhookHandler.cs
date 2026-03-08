using System.Text.Json.Serialization;

namespace Aevatar.App.Application.Services;

public interface IRevenueCatWebhookHandler
{
    Task HandleAsync(RevenueCatWebhookPayload payload);
}

public sealed class RevenueCatWebhookPayload
{
    [JsonPropertyName("api_version")]
    public string? ApiVersion { get; set; }

    [JsonPropertyName("event")]
    public RevenueCatEvent? Event { get; set; }
}

public sealed class RevenueCatEvent
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("app_user_id")]
    public string? AppUserId { get; set; }

    [JsonPropertyName("transaction_id")]
    public string? TransactionId { get; set; }

    [JsonPropertyName("original_transaction_id")]
    public string? OriginalTransactionId { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("price_in_purchased_currency")]
    public decimal PriceInPurchasedCurrency { get; set; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    [JsonPropertyName("store")]
    public string? Store { get; set; }

    [JsonPropertyName("cancel_reason")]
    public string? CancelReason { get; set; }

    [JsonPropertyName("purchased_at_ms")]
    public long? PurchasedAtMs { get; set; }

    [JsonPropertyName("event_timestamp_ms")]
    public long EventTimestampMs { get; set; }
}