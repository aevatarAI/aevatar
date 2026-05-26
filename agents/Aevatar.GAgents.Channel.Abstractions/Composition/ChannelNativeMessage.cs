namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Platform-neutral channel-native message payload produced by a composer for transport dispatch.
/// </summary>
/// <remarks>
/// Composers translate channel-agnostic <see cref="MessageContent"/> intents into this unified DTO so
/// that transports (for example, NyxID relay) can dispatch outbound replies without knowing which
/// adapter produced the payload. <see cref="Text"/> carries the plain-text fallback; <see cref="CardPayload"/>
/// carries the adapter-native rich card payload when the intent materializes as an interactive card.
/// </remarks>
public sealed record ChannelNativeMessage(
    string? Text,
    object? CardPayload,
    string? MessageType,
    ComposeCapability Capability)
{
    /// <summary>Gets a value indicating whether the composed payload carries a rich interactive card.</summary>
    public bool IsInteractive => CardPayload is not null;
}
