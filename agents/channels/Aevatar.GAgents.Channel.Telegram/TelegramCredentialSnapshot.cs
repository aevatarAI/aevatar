using System.Text.Json;

namespace Aevatar.GAgents.Channel.Telegram;

internal sealed record TelegramCredentialSnapshot(string BotToken)
{
    public static TelegramCredentialSnapshot Parse(string? rawSecret)
    {
        if (string.IsNullOrWhiteSpace(rawSecret))
            return new TelegramCredentialSnapshot(string.Empty);

        var trimmed = rawSecret.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            return new TelegramCredentialSnapshot(trimmed);

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            return new TelegramCredentialSnapshot(
                ReadFirst(root, "bot_token", "token", "access_token"));
        }
        catch (JsonException)
        {
            return new TelegramCredentialSnapshot(trimmed);
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
