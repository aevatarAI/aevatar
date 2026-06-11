using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Lark.Tools;

internal static class LarkMessageIdResolver
{
    private const string ChannelMessageIdKey = "channel.message_id";
    private const string ChannelPlatformMessageIdKey = "channel.platform_message_id";

    // Refactor (issue1378/first-slice):
    //   Old pattern: ResolveOrCurrent used current message when explicit message_id was missing.
    //   New principle: ResolveExplicit returns structured error; external tools path remains.
    public static string? ResolveExplicit(string? messageId, out string? error)
    {
        error = null;

        if (TryValidate(messageId, out var explicitMessageId, out var explicitError))
            return explicitMessageId;

        error = !string.IsNullOrWhiteSpace(explicitError)
            ? explicitError
            : "message_id is required";
        return null;
    }

    public static string? ResolveOrCurrent(string? messageId, out bool usedCurrentMessage, out string? error)
    {
        usedCurrentMessage = false;
        error = null;

        if (TryValidate(messageId, out var explicitMessageId, out var explicitError))
            return explicitMessageId;

        if (!string.IsNullOrWhiteSpace(explicitError))
        {
            error = explicitError;
            return null;
        }

        var currentMessageId = AgentToolRequestContext.ChannelPlatformMessageId ??
                               AgentToolRequestContext.ChannelMessageId ??
                               AgentToolRequestContext.TryGetExternalMetadata(ChannelPlatformMessageIdKey) ??
                               AgentToolRequestContext.TryGetExternalMetadata(ChannelMessageIdKey);
        if (TryValidate(currentMessageId, out var current, out _))
        {
            usedCurrentMessage = true;
            return current;
        }

        if (!string.IsNullOrWhiteSpace(currentMessageId))
        {
            error = "Current turn metadata did not expose a Lark platform message_id (expected om_xxx). Pass message_id explicitly.";
            return null;
        }

        error = "message_id is required when current turn metadata does not include a Lark message id";
        return null;
    }

    public static bool TryValidate(string? raw, out string? normalized, out string? error)
    {
        normalized = raw?.Trim();
        error = null;
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (!normalized.StartsWith("om_", StringComparison.OrdinalIgnoreCase))
        {
            error = "message_id must be a Lark message id like om_xxx";
            normalized = null;
            return false;
        }

        return true;
    }
}

internal static class LarkReactionEmojiHelper
{
    private const string DefaultEmojiType = "OK";

    private static readonly Dictionary<string, string> EmojiAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ok"] = "OK",
        ["okay"] = "OK",
        ["了解"] = "OK",
        ["收到"] = "OK",
        ["thumbsup"] = "THUMBSUP",
        ["thumbs_up"] = "THUMBSUP",
        ["赞"] = "THUMBSUP",
        ["点赞"] = "THUMBSUP",
        ["done"] = "DONE",
        ["完成"] = "DONE",
        ["smile"] = "SMILE",
        ["微笑"] = "SMILE",
    };

    public static string NormalizeOrDefault(string? emojiType)
    {
        var normalized = NormalizeOptional(emojiType);
        return string.IsNullOrWhiteSpace(normalized) ? DefaultEmojiType : normalized;
    }

    public static string? NormalizeOptional(string? emojiType)
    {
        var trimmed = emojiType?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        return EmojiAliases.TryGetValue(trimmed, out var alias)
            ? alias
            : trimmed;
    }
}
