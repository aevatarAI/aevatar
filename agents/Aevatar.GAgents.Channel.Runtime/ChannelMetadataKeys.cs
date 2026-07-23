namespace Aevatar.GAgents.Channel.Runtime;

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
    /// Provider slug used for outbound delivery back to the current channel. This is the generic
    /// channel-delivery route selected by the inbound adapter.
    /// </summary>
    public const string OutboundProviderSlug = "channel.outbound.provider_slug";
    /// <summary>Provider-interpreted primary outbound address for the current channel turn.</summary>
    public const string DeliveryAddressId = "channel.delivery.address_id";
    /// <summary>Provider-interpreted type for <see cref="DeliveryAddressId"/>.</summary>
    public const string DeliveryAddressType = "channel.delivery.address_type";
    /// <summary>Optional provider-interpreted fallback outbound address for the current channel turn.</summary>
    public const string DeliveryFallbackAddressId = "channel.delivery.fallback_address_id";
    /// <summary>Provider-interpreted type for <see cref="DeliveryFallbackAddressId"/>.</summary>
    public const string DeliveryFallbackAddressType = "channel.delivery.fallback_address_type";
    /// <summary>
    /// Everyone @-mentioned in the inbound message, as a readable list of <c>name &lt;canonical_id&gt;</c>
    /// entries (on Lark the canonical id is the mentioned party's <c>open_id</c>), in the order their
    /// <c>@_user_N</c> placeholders appear in the message text. Surfaced into the agent's
    /// <c>&lt;channel-context&gt;</c> so the agent can resolve a third-party mention to a real id instead
    /// of misusing the literal <c>@_user_N</c> placeholder as a member id. Absent when no one is mentioned.
    /// </summary>
    public const string Mentions = "channel.mentions";
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
    /// Lark <c>user_id</c> of the card-action operator. This identifies the user who clicked or
    /// submitted an interactive card action.
    /// </summary>
    public const string LarkOperatorUserId = "channel.lark.operator_user_id";
    /// <summary>Lark <c>open_id</c> of the card-action operator when available.</summary>
    public const string LarkOperatorOpenId = "channel.lark.operator_open_id";
    /// <summary>Lark <c>union_id</c> of the card-action operator when available.</summary>
    public const string LarkOperatorUnionId = "channel.lark.operator_union_id";
    /// <summary>
    /// Lark <c>user_id</c> of the ordinary-message sender resolved from Lark contact lookup.
    /// Distinct from <see cref="LarkOperatorUserId"/>, which is scoped to card actions.
    /// </summary>
    public const string LarkSubjectUserId = "channel.lark.subject_user_id";
    /// <summary>
    /// Lark <c>employee_id</c> of the ordinary-message sender resolved from Lark contact lookup.
    /// Distinct from card-action operator identity.
    /// </summary>
    public const string LarkSubjectEmployeeId = "channel.lark.subject_employee_id";
    /// <summary>
    /// NyxID provider slug of the inbound channel-bot that received this turn's webhook
    /// event. Equivalent to <c>ChannelInboundEvent.NyxProviderSlug</c>, surfaced as request
    /// metadata so scheduled workflow creation can capture a failure-notification provider.
    /// </summary>
    /// <remarks>
    /// The inbound channel-bot is the bot the user just successfully messaged. When the
    /// agent's primary outbound proxy fails with a structural rejection (e.g. Lark
    /// <c>99992364 user id cross tenant</c> from a cross-tenant relay/outbound mismatch),
    /// the inbound bot's slug is the only known proxy that can still deliver to the user.
    /// Scheduled workflow creation uses this for failure notifications only; primary outbound
    /// stays on the caller-provided provider slug so existing deployments are not rerouted
    /// unexpectedly. See issue #423 §C.
    /// </remarks>
    public const string InboundChannelBotProxySlug = "channel.inbound.channel_bot_provider_slug";
}
