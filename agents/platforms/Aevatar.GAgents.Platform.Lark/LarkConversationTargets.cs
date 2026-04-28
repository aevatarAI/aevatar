namespace Aevatar.GAgents.Platform.Lark;

public static class LarkConversationTargets
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
    /// <b>Priority order (all conversation types):</b> <c>chat_id</c> &gt; <c>union_id</c> &gt;
    /// <c>open_id</c>. <c>chat_id</c> (<c>oc_*</c>) is the most direct identifier — for DMs it is
    /// the literal chat thread between the user and the bot that received the inbound event, so
    /// when the outbound proxy authenticates as the SAME Lark app, sending back via
    /// <c>receive_id_type=chat_id</c> targets the same chat without traversing any user-id
    /// resolution. <c>union_id</c> is tenant-scoped, valid across apps in one tenant but rejected
    /// cross-tenant (<c>code:99992364 user id cross tenant</c>). <c>open_id</c> is app-scoped and
    /// rejected even cross-app within the same tenant (<c>code:99992361 open_id cross app</c>).
    /// </para>
    ///
    /// <para>
    /// Earlier revisions inverted this for p2p (preferring <c>union_id</c>) on the assumption
    /// that DM <c>chat_id</c> is bot-specific and the relay-side ingress bot might differ from
    /// the outbound app. Production logs from PR #409 showed the opposite failure mode in this
    /// deployment (NyxID's <c>s/api-lark-bot</c> proxy and the relay-side ingress are in
    /// different tenants), so <c>union_id</c> hits <c>cross tenant</c> for the typical case.
    /// <c>chat_id</c> works whenever the outbound app matches the ingress app — the most common
    /// real configuration — and degrades cleanly to <c>union_id</c> / <c>open_id</c> otherwise.
    /// </para>
    ///
    /// <para>
    /// If none of the typed identifiers are available (no chat_id, no union_id, no senderId),
    /// returning a typed pair would silently re-create the original /daily 400 (typing the
    /// conversation_id as <c>chat_id</c>). Instead return an empty typed pair with
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
        // Most-direct first: the actual Lark chat the inbound was received in. Tenant-scoped
        // and survives cross-app-within-tenant configurations as long as the outbound app is
        // also a member of the chat — which the relay-side ingress bot is by construction (it
        // received the message there).
        var trimmedChat = (larkChatId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmedChat))
            return new LarkReceiveTarget(trimmedChat, DefaultReceiveIdType, FellBackToPrefixInference: false);

        if (IsDirectMessage(chatType))
        {
            // Tenant-stable user identifier. Surfaces `code:99992364 user id cross tenant` when
            // the relay-side ingress and outbound apps are in different tenants — flag the
            // fallback so call sites can LogDebug for incident correlation.
            var trimmedUnion = (larkUnionId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(trimmedUnion))
                return new LarkReceiveTarget(trimmedUnion, UnionIdReceiveIdType, FellBackToPrefixInference: true);

            // App-scoped open_id. Surfaces `code:99992361 open_id cross app` when the apps
            // differ even within the same tenant.
            var trimmedSender = (senderId ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(trimmedSender))
                return new LarkReceiveTarget(trimmedSender, OpenIdReceiveIdType, FellBackToPrefixInference: true);

            return new LarkReceiveTarget(string.Empty, string.Empty, FellBackToPrefixInference: true);
        }

        // Non-DM with no Lark chat_id surfaced: assume the routing conversation_id is a Lark
        // `oc_*` (legacy behavior pre ingress-side chat_id capture). If it is not, the proxy
        // will reject and the call site logs the surfaced Lark error.
        var trimmedConversation = (conversationId ?? string.Empty).Trim();
        return new LarkReceiveTarget(trimmedConversation, DefaultReceiveIdType, FellBackToPrefixInference: false);
    }

    // Only "p2p" is emitted by ChannelConversationTurnRunner.ResolveConversationChatType today,
    // which is the single source for ChannelMetadataKeys.ChatType in this repo. Keep the check
    // narrow until a second emitter (e.g. a Telegram bridge) actually lands.
    private static bool IsDirectMessage(string? chatType) =>
        string.Equals(chatType?.Trim(), "p2p", StringComparison.Ordinal);

    /// <summary>
    /// Builds the primary outbound delivery target plus a secondary fallback. The primary
    /// follows <see cref="BuildFromInbound"/>'s priority (chat_id &gt; union_id &gt; open_id).
    /// The fallback is the next-best identifier we have at ingress time so the runtime can
    /// retry once on a Lark <c>230002 bot not in chat</c> rejection without needing a fresh
    /// ingress event:
    /// <list type="bullet">
    /// <item><description>For p2p: when the primary is chat_id (`oc_*`), the fallback is union_id (`on_*`) when the relay surfaced one. This recovers cross-app same-tenant deployments where the outbound app is not in the DM chat.</description></item>
    /// <item><description>For groups: no fallback — chat_id is tenant-scoped and either works (any app in the chat) or fails for reasons that union_id wouldn't fix.</description></item>
    /// </list>
    /// </summary>
    public static LarkReceiveTargetWithFallback BuildFromInboundWithFallback(
        string? chatType,
        string? conversationId,
        string? senderId,
        string? larkUnionId = null,
        string? larkChatId = null)
    {
        var primary = BuildFromInbound(chatType, conversationId, senderId, larkUnionId, larkChatId);

        // Only useful when the primary is chat_id AND we still have a tenant-stable user
        // identifier to try in cross-app same-tenant scenarios. Skip when the primary is
        // already union_id or open_id — those don't need a fallback because they are NOT
        // app-specific in the way DM chat_id is.
        if (!IsDirectMessage(chatType))
            return new LarkReceiveTargetWithFallback(primary, Fallback: null);

        if (!string.Equals(primary.ReceiveIdType, DefaultReceiveIdType, StringComparison.Ordinal))
            return new LarkReceiveTargetWithFallback(primary, Fallback: null);

        var trimmedUnion = (larkUnionId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(trimmedUnion))
            return new LarkReceiveTargetWithFallback(
                primary,
                new LarkReceiveTarget(trimmedUnion, UnionIdReceiveIdType, FellBackToPrefixInference: false));

        return new LarkReceiveTargetWithFallback(primary, Fallback: null);
    }
}

public readonly record struct LarkReceiveTarget(
    string ReceiveId,
    string ReceiveIdType,
    bool FellBackToPrefixInference);

/// <summary>
/// Primary outbound delivery target plus a secondary fallback. Captured at agent-create time
/// when the inbound surfaces both a chat_id (primary) and a union_id (fallback). The runtime
/// tries the primary first; on a Lark <c>230002 bot not in chat</c> rejection — the failure
/// mode for cross-app same-tenant deployments where the outbound app is not a member of the
/// inbound DM chat — it retries once with the fallback. Without the fallback, switching to
/// chat_id-first would regress those deployments because chat_id is bot-specific for DMs and
/// only valid when the same Lark app received the inbound.
/// </summary>
public readonly record struct LarkReceiveTargetWithFallback(
    LarkReceiveTarget Primary,
    LarkReceiveTarget? Fallback);
