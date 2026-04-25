namespace Aevatar.GAgents.ChannelRuntime;

internal static class LarkConversationTargets
{
    private const string DefaultReceiveIdType = "chat_id";
    private const string OpenIdReceiveIdType = "open_id";
    private const string UnionIdReceiveIdType = "union_id";

    /// <summary>
    /// Resolves the Lark <c>receive_id</c> + <c>receive_id_type</c> pair to use for an outbound
    /// proxy call. When the typed fields captured at delivery-target creation are both populated,
    /// they are returned verbatim. Otherwise — only for legacy state persisted before the typed
    /// fields existed — the helper falls back to inferring the type from the prefix of
    /// <paramref name="legacyConversationId"/>. The <c>FellBackToPrefixInference</c> flag lets
    /// call sites emit a breadcrumb so format drift is observable instead of silently rejected
    /// by Lark.
    /// </summary>
    public static LarkReceiveTarget Resolve(
        string? typedReceiveId,
        string? typedReceiveIdType,
        string? legacyConversationId)
    {
        var trimmedTypedId = (typedReceiveId ?? string.Empty).Trim();
        var trimmedTypedType = (typedReceiveIdType ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmedTypedId) && !string.IsNullOrEmpty(trimmedTypedType))
        {
            return new LarkReceiveTarget(
                trimmedTypedId,
                trimmedTypedType,
                FellBackToPrefixInference: false);
        }

        var trimmedLegacy = (legacyConversationId ?? string.Empty).Trim();
        return new LarkReceiveTarget(
            trimmedLegacy,
            ResolveReceiveIdType(trimmedLegacy),
            FellBackToPrefixInference: true);
    }

    /// <summary>
    /// Picks a Lark <c>receive_id_type</c> by prefix. Public only so tests and callers that have
    /// already committed to the legacy <c>conversation_id</c> field can use it; new code paths
    /// should prefer <see cref="Resolve"/> with the typed fields.
    /// </summary>
    public static string ResolveReceiveIdType(string? conversationId)
    {
        var trimmed = conversationId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return DefaultReceiveIdType;

        if (trimmed.StartsWith("ou_", StringComparison.Ordinal))
            return OpenIdReceiveIdType;
        if (trimmed.StartsWith("on_", StringComparison.Ordinal))
            return UnionIdReceiveIdType;

        return DefaultReceiveIdType;
    }

    /// <summary>
    /// Builds the typed receive-target for a Lark inbound captured at agent creation. For p2p we
    /// store the user's open_id (always <c>ou_*</c>) so outbound DMs do not depend on the relay
    /// also propagating an underlying chat_id; for everything else we send to the originating
    /// chat via its <c>oc_*</c> chat_id, which Lark accepts uniformly for groups, threads, and
    /// channels.
    /// </summary>
    public static LarkReceiveTarget BuildFromInbound(string? chatType, string? conversationId, string? senderId)
    {
        var trimmedSender = (senderId ?? string.Empty).Trim();
        if (IsDirectMessage(chatType) && !string.IsNullOrEmpty(trimmedSender))
        {
            return new LarkReceiveTarget(trimmedSender, OpenIdReceiveIdType, FellBackToPrefixInference: false);
        }

        var trimmedConversation = (conversationId ?? string.Empty).Trim();
        return new LarkReceiveTarget(trimmedConversation, DefaultReceiveIdType, FellBackToPrefixInference: false);
    }

    private static bool IsDirectMessage(string? chatType)
    {
        if (string.IsNullOrWhiteSpace(chatType))
            return false;

        var normalized = chatType.Trim();
        return string.Equals(normalized, "p2p", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "direct_message", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "directmessage", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "dm", StringComparison.OrdinalIgnoreCase);
    }
}

internal readonly record struct LarkReceiveTarget(
    string ReceiveId,
    string ReceiveIdType,
    bool FellBackToPrefixInference);
