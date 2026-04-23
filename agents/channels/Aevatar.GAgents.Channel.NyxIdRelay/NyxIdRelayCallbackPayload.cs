using System.Text.Json.Serialization;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayCallbackPayload
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("agent")]
    public NyxIdRelayAgentPayload? Agent { get; set; }

    [JsonPropertyName("conversation")]
    public NyxIdRelayConversationPayload? Conversation { get; set; }

    [JsonPropertyName("sender")]
    public NyxIdRelaySenderPayload? Sender { get; set; }

    [JsonPropertyName("content")]
    public NyxIdRelayContentPayload? Content { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
}

public sealed class NyxIdRelayAgentPayload
{
    [JsonPropertyName("api_key_id")]
    public string? ApiKeyId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class NyxIdRelayConversationPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("platform_id")]
    public string? PlatformId { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("conversation_type")]
    public string? ConversationType { get; set; }
}

public sealed class NyxIdRelaySenderPayload
{
    [JsonPropertyName("platform_id")]
    public string? PlatformId { get; set; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

public sealed class NyxIdRelayContentPayload
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
