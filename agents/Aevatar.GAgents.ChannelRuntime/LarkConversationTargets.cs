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
    /// Builds the typed receive-target for a Lark inbound captured at agent creation.
    ///
    /// <para>
    /// <b>p2p (DM):</b> prefer the tenant-stable <c>union_id</c> (<c>on_*</c>) when the relay
    /// surfaces it. <c>union_id</c> is cross-app safe within the tenant — Lark accepts it as a
    /// <c>receive_id_type=union_id</c> target regardless of whether the relay-side ingress app
    /// matches the customer's outbound app. Without union_id we fall back to the sender
    /// <c>open_id</c> (<c>ou_*</c>), which is app-scoped and produces
    /// <c>code:99992361 open_id cross app</c> when the two apps differ; the fallback flips
    /// <c>FellBackToPrefixInference=true</c> so the call site emits a Debug breadcrumb and
    /// operators can correlate Lark rejections with missing-union_id ingress.
    /// </para>
    ///
    /// <para>
    /// <b>group / channel / thread:</b> prefer the inbound Lark <c>chat_id</c> (<c>oc_*</c>) which
    /// is tenant-scoped — any app added to the chat can address it via
    /// <c>receive_id_type=chat_id</c>. Without an explicit Lark chat_id the helper falls back to
    /// the routing <paramref name="conversationId"/>, which works only when the routing id is
    /// itself a Lark <c>oc_*</c>; otherwise the outbound proxy will surface a Lark validation
    /// failure that the call site logs and retries.
    /// </para>
    ///
    /// <para>
    /// If the inbound is p2p but the relay omitted both <c>union_id</c> and <c>senderId</c>,
    /// returning a typed pair would silently re-create the original /daily 400 (typing the user
    /// open_id as <c>chat_id</c>). Instead, return an empty typed pair with
    /// <c>FellBackToPrefixInference=true</c> so <see cref="Resolve"/> falls back to the legacy
    /// prefix path and call sites emit a Debug breadcrumb.
    /// </para>
    /// </summary>
    public static LarkReceiveTarget BuildFromInbound(
        string? chatType,
        string? conversationId,
        string? senderId,
        string? larkUnionId = null,
        string? larkChatId = null)
    {
        if (IsDirectMessage(chatType))
        {
            // Cross-app safe: tenant-stable user identifier, accepted by any Lark app.
            var trimmedUnion = (larkUnionId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(trimmedUnion))
                return new LarkReceiveTarget(trimmedUnion, UnionIdReceiveIdType, FellBackToPrefixInference: false);

            // Fallback: app-scoped open_id. Will surface `code:99992361 open_id cross app` from
            // Lark when the relay-side ingress app does not match the customer's outbound app.
            // Flag the fallback so call sites can LogDebug for incident correlation.
            var trimmedSender = (senderId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(trimmedSender))
                return new LarkReceiveTarget(trimmedSender, OpenIdReceiveIdType, FellBackToPrefixInference: true);

            return new LarkReceiveTarget(string.Empty, string.Empty, FellBackToPrefixInference: true);
        }

        // group / channel / thread: prefer the inbound Lark chat_id (cross-app within tenant).
        var trimmedChat = (larkChatId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmedChat))
            return new LarkReceiveTarget(trimmedChat, DefaultReceiveIdType, FellBackToPrefixInference: false);

        // Fallback: assume the routing conversation_id is a Lark `oc_*` (legacy behavior pre
        // ingress-side chat_id capture). If it is not, the proxy will reject and the call site
        // logs the surfaced Lark error.
        var trimmedConversation = (conversationId ?? string.Empty).Trim();
        return new LarkReceiveTarget(trimmedConversation, DefaultReceiveIdType, FellBackToPrefixInference: false);
    }

    // Only "p2p" is emitted by ChannelConversationTurnRunner.ResolveConversationChatType today,
    // which is the single source for ChannelMetadataKeys.ChatType in this repo. Keep the check
    // narrow until a second emitter (e.g. a Telegram bridge) actually lands.
    private static bool IsDirectMessage(string? chatType) =>
        string.Equals(chatType?.Trim(), "p2p", StringComparison.Ordinal);
}

internal readonly record struct LarkReceiveTarget(
    string ReceiveId,
    string ReceiveIdType,
    bool FellBackToPrefixInference);
