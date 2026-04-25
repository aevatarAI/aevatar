namespace Aevatar.GAgents.ChannelRuntime;

internal static class LarkConversationTargets
{
    private const string DefaultReceiveIdType = "chat_id";

    public static string ResolveReceiveIdType(string? conversationId)
    {
        var trimmed = conversationId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return DefaultReceiveIdType;

        if (trimmed.StartsWith("ou_", StringComparison.Ordinal))
            return "open_id";
        if (trimmed.StartsWith("on_", StringComparison.Ordinal))
            return "union_id";

        return DefaultReceiveIdType;
    }
}
