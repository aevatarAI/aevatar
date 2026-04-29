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
    /// <summary>
    /// NyxID outbound proxy slug used to deliver Lark messages from inside a workflow run
    /// (default <c>api-lark-bot</c>). The <c>outbound</c> qualifier is deliberate — this is
    /// specifically the routing target for Lark <em>send</em> calls (e.g.
    /// <c>open-apis/im/v1/messages</c>) initiated by the workflow runtime, not a generic Lark
    /// API field. PR #461 review item #4 flagged the original name (<c>channel.lark.proxy_slug</c>)
    /// as ambiguous between "Lark API surface" and "NyxID provider routing" — the
    /// <c>outbound_proxy_slug</c> form makes the routing-side semantics explicit.
    /// </summary>
    public const string LarkOutboundProxySlug = "channel.lark.outbound_proxy_slug";

    /// <summary>
    /// NyxID provider slug of the inbound channel-bot that received this turn's webhook
    /// event. Equivalent to <c>ChannelInboundEvent.NyxProviderSlug</c>, surfaced as request
    /// metadata so the agent-builder tool can capture it on the new agent's
    /// <c>SkillRunnerOutboundConfig.FailureNotificationProviderSlug</c> at create time.
    /// </summary>
    /// <remarks>
    /// The inbound channel-bot is the bot the user just successfully messaged. When the
    /// agent's primary outbound proxy fails with a structural rejection (e.g. Lark
    /// <c>99992364 user id cross tenant</c> from a cross-tenant relay/outbound mismatch),
    /// the inbound bot's slug is the only known proxy that can still deliver to the user.
    /// SkillRunner uses this for failure notifications only — primary outbound stays on the
    /// caller-provided <c>nyx_provider_slug</c> argument so existing deployments are not
    /// rerouted unexpectedly. See issue #423 §C.
    /// </remarks>
    public const string InboundChannelBotProxySlug = "channel.inbound.channel_bot_provider_slug";
}
