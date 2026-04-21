using System.Text.Json;

namespace Aevatar.GAgents.Channel.Lark;

internal sealed record LarkCredentialSnapshot(
    string AccessToken,
    string EncryptKey)
{
    public static LarkCredentialSnapshot Parse(string? rawSecret)
    {
        if (string.IsNullOrWhiteSpace(rawSecret))
            return new LarkCredentialSnapshot(string.Empty, string.Empty);

        var trimmed = rawSecret.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            return new LarkCredentialSnapshot(trimmed, string.Empty);

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            return new LarkCredentialSnapshot(
                ReadFirst(root, "tenant_access_token", "access_token", "bot_token", "token"),
                ReadFirst(root, "encrypt_key", "encryptKey"));
        }
        catch (JsonException)
        {
            return new LarkCredentialSnapshot(trimmed, string.Empty);
        }
    }

    private static string ReadFirst(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }
}
