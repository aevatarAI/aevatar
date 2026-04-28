namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Typed metadata keys for channel runtime context.
/// Used in ChatRequestEvent.Metadata to pass channel-specific context to downstream actors.
/// </summary>
public static class ChannelMetadataKeys
{
    public const string Platform = "channel.platform";
    public const string SenderId = "channel.sender_id";
    /// <summary>
    /// The bot's registration scope id (per-NyxID-account; one bot = one scope). Carries
    /// the inbound channel registration's scope so caller-scope resolution and tools can
    /// route per-bot operations consistently. The literal "scope_id" string was used
    /// historically across multiple call sites; this typed constant exists so future
    /// renames don't have to chase string literals (issue #466 review).
    /// </summary>
    public const string RegistrationScopeId = "scope_id";
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
}
