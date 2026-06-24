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
}
