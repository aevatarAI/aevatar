using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed class NyxIdRelayCallbackPayload
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("platform_message_id")]
    public string? PlatformMessageId { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("reply_token")]
    public string? ReplyToken { get; set; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("reply_to_platform_message_id")]
    public string? ReplyToPlatformMessageId { get; set; }

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

    [JsonPropertyName("raw_platform_data")]
    public JsonElement? RawPlatformData { get; set; }
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

    [JsonPropertyName("attachments")]
    public List<NyxIdRelayAttachmentPayload>? Attachments { get; set; }
}

public sealed class NyxIdRelayAttachmentPayload
{
    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("platform_message_id")]
    public string? PlatformMessageId { get; set; }

    [JsonPropertyName("file_key")]
    public string? FileKey { get; set; }

    [JsonPropertyName("image_key")]
    public string? ImageKey { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("mime_type")]
    public string? MimeType { get; set; }

    [JsonPropertyName("size_bytes")]
    public long? SizeBytes { get; set; }
}
