using System.Diagnostics;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Shared OpenTelemetry / System.Diagnostics primitives for the channel runtime.
/// Span names follow RFC §6.1 mandatory spans and tags follow the mandatory dimensions contract.
/// </summary>
public static class ChannelDiagnostics
{
    /// <summary>
    /// The single <see cref="ActivitySource"/> name subscribed by OTEL pipelines for channel
    /// runtime spans.
    /// </summary>
    public const string ActivitySourceName = "Aevatar.Channel";

    /// <summary>
    /// The semver-like version string attached to the activity source.
    /// </summary>
    public const string ActivitySourceVersion = "1.0.0";

    /// <summary>
    /// Shared <see cref="ActivitySource"/> instance emitted by all channel-runtime components.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, ActivitySourceVersion);

    /// <summary>
    /// Mandatory span names per RFC §6.1. Callers use these so dashboards can rely on stable names.
    /// </summary>
    public static class Spans
    {
        /// <summary>Ingress: activity received by adapter (prior to durable inbox commit).</summary>
        public const string IngressVerify = "channel.ingress.verify";

        /// <summary>Ingress: durable-inbox commit completed.</summary>
        public const string IngressCommit = "channel.ingress.commit";

        /// <summary>Pipeline-wide top-level span that wraps dedup + resolve + bot.turn + egress.</summary>
        public const string PipelineInvoke = "channel.pipeline.invoke";

        /// <summary>Pipeline: authoritative dedup check inside conversation grain turn.</summary>
        public const string PipelineDedup = "channel.pipeline.dedup";

        /// <summary>Pipeline: resolve conversation-scoped context + target grain.</summary>
        public const string PipelineResolve = "channel.pipeline.resolve";

        /// <summary>Bot-turn span: bot logic execution between middleware and egress.</summary>
        public const string BotTurn = "channel.bot.turn";

        /// <summary>Egress send.</summary>
        public const string EgressSend = "channel.egress.send";

        /// <summary>Egress update.</summary>
        public const string EgressUpdate = "channel.egress.update";

        /// <summary>Egress delete.</summary>
        public const string EgressDelete = "channel.egress.delete";

        /// <summary>Egress commit — durable confirmation that an emit has been recorded.</summary>
        public const string EgressCommit = "channel.egress.commit";
    }

    /// <summary>
    /// Mandatory tag names per RFC §6.1. Callers use these so observability joins consistently.
    /// </summary>
    public static class Tags
    {
        /// <summary>Normalized inbound activity id.</summary>
        public const string ActivityId = "activity_id";

        /// <summary>Adapter-provided event id (raw-payload identifier).</summary>
        public const string ProviderEventId = "provider_event_id";

        /// <summary>ConversationReference canonical key.</summary>
        public const string CanonicalKey = "canonical_key";

        /// <summary>Bot instance id.</summary>
        public const string BotInstanceId = "bot_instance_id";

        /// <summary>Sent activity id (set after outbound success).</summary>
        public const string SentActivityId = "sent_activity_id";

        /// <summary>Retry attempt count.</summary>
        public const string RetryCount = "retry_count";

        /// <summary>Redacted raw payload blob ref.</summary>
        public const string RawPayloadBlobRef = "raw_payload_blob_ref";

        /// <summary>Auth principal kind + id summary (e.g. <c>bot</c> or <c>user:u1</c>).</summary>
        public const string AuthPrincipal = "auth_principal";

        /// <summary>Channel id.</summary>
        public const string ChannelId = "channel_id";
    }
}
