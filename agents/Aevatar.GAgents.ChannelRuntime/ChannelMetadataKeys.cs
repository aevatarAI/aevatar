namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Typed metadata keys for channel runtime context.
/// Used in ChatRequestEvent.Metadata to pass channel-specific context to downstream actors.
/// </summary>
public static class ChannelMetadataKeys
{
    public const string Platform = "channel.platform";
    public const string SenderId = "channel.sender_id";
    public const string SenderName = "channel.sender_name";
    public const string ConversationId = "channel.conversation_id";
    public const string MessageId = "channel.message_id";
    public const string PlatformMessageId = "channel.platform_message_id";
    public const string ChatType = "channel.chat_type";
    /// <summary>
    /// Lark <c>union_id</c> (<c>on_*</c>) of the inbound sender. Tenant-stable and cross-app safe;
    /// downstream Lark senders prefer this over <see cref="SenderId"/> (<c>open_id</c>) for p2p
    /// outbound delivery so a relay-app vs outbound-app mismatch does not produce
    /// <c>open_id cross app</c> rejections from Lark. Empty when the platform is not Lark or the
    /// relay did not surface a <c>union_id</c>.
    /// </summary>
    public const string LarkUnionId = "channel.lark.union_id";
    /// <summary>
    /// Lark <c>chat_id</c> (<c>oc_*</c>) as observed by the relay-side Lark app. Cross-app safe
    /// within the tenant for groups/threads/channels. Downstream Lark senders prefer this for
    /// non-p2p outbound delivery instead of inferring a chat_id from the routing
    /// <see cref="ConversationId"/> (which may be a NyxID-internal route id).
    /// </summary>
    public const string LarkChatId = "channel.lark.chat_id";
    /// <summary>
    /// Authoritative outbound Lark <c>receive_id</c> for the current workflow run, captured at
    /// agent-create time. Propagated via <c>WorkflowChatRunRequest.Metadata</c> so workflow
    /// modules (e.g. <c>TwitterPublishModule</c>) can surface their result back into the same
    /// chat without having to look up the catalog at execution time.
    /// </summary>
    public const string LarkReceiveId = "channel.lark.receive_id";
    /// <summary>Companion to <see cref="LarkReceiveId"/> — its <c>receive_id_type</c>.</summary>
    public const string LarkReceiveIdType = "channel.lark.receive_id_type";
    /// <summary>NyxID outbound proxy slug used to deliver Lark messages (default <c>api-lark-bot</c>).</summary>
    public const string LarkProxySlug = "channel.lark.proxy_slug";
}
