using System.Text.Json;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdRelayPayloads
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static string NormalizeContentType(string? contentType) =>
        NormalizeOptional(contentType)?.ToLowerInvariant() ?? string.Empty;

    public static string GetContentType(RelayContent? content)
    {
        var normalized = NormalizeContentType(content?.ContentType);
        return !string.IsNullOrWhiteSpace(normalized)
            ? normalized
            : NormalizeContentType(content?.Type);
    }

    public static string? NormalizeOptional(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}

internal sealed class RelayMessage
{
    public string? MessageId { get; set; }
    public string? Platform { get; set; }
    public RelayAgent? Agent { get; set; }
    public RelayConversation? Conversation { get; set; }
    public RelaySender? Sender { get; set; }
    public RelayContent? Content { get; set; }
    public string? Timestamp { get; set; }
}

internal sealed class RelayAgent
{
    public string? ApiKeyId { get; set; }
    public string? Name { get; set; }
}

internal sealed class RelayConversation
{
    public string? Id { get; set; }
    public string? PlatformId { get; set; }
    public string? Type { get; set; }
}

internal sealed class RelaySender
{
    public string? PlatformId { get; set; }
    public string? DisplayName { get; set; }
}

internal sealed class RelayContent
{
    public string? ContentType { get; set; }
    public string? Type { get; set; }
    public string? Text { get; set; }
}
