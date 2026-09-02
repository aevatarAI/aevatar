namespace Aevatar.AI.Abstractions.ToolProviders;

/// <summary>
/// Well-known capability tokens declared by tools through
/// <see cref="IAgentToolCapabilityDescriptor"/>. Consumers filter/select tools by
/// matching these generic, self-describing tokens — never by hardcoding a specific
/// tool name — so a tool's eligibility for a given surface is a property of the tool
/// itself, not knowledge baked into the consumer.
/// </summary>
public static class AgentToolCapabilities
{
    /// <summary>
    /// Surface signal: a tool carrying this capability is NOT appropriate for a direct
    /// channel/chat conversation agent (e.g. the Lark/NyxID reply path). Such a tool
    /// completes its work somewhere other than the live chat — typically delivering its
    /// outcome to a separate observability/console surface — so offering it on a chat
    /// agent's tool surface would let the model silently route a chat user's request away
    /// from the chat. The tool stays in the global catalog (the workflow allowlist path
    /// can still select it); only direct-channel discovery filters it out.
    /// </summary>
    public const string ExcludeFromDirectChannelChat = "surface.exclude_from_direct_channel_chat";

    /// <summary>
    /// Surface signal: a tool carrying this capability is not offered by the NyxID Assistant.
    /// The tool remains available to workflows, direct channels, and other consumers that own
    /// their own admission contract.
    /// </summary>
    public const string ExcludeFromNyxIdChat = "surface.exclude_from_nyxid_chat";

    /// <summary>
    /// Surface signal: a tool carrying this capability targets a broker surface that only accepts a
    /// human-session credential. In a channel-relay turn the effective credential is a bot-class
    /// relay/API-key token, which the broker rejects (403) on those surfaces — so the tool can only
    /// fail if offered. Direct-channel discovery filters it out of channel-relay turns (it is never
    /// offered to the model and never registered, so it cannot be invoked); console/studio
    /// human-session turns keep the full set, and the tool stays in the global catalog for other
    /// surfaces. Eligibility is a property of the tool, so the channel path stays name-agnostic.
    /// </summary>
    public const string RequiresHumanSession = "surface.requires_human_session";
}
