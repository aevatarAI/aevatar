namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// One capability-flag ↔ optional-interface parity assertion supplied by an adapter.
/// </summary>
/// <param name="CapabilityName">
/// Descriptive name of the capability (e.g. <c>"SupportsTyping"</c>). Surfaced in assertion failure messages.
/// </param>
/// <param name="CapabilityFlag">The current value of the capability flag on the adapter's <c>ChannelCapabilities</c>.</param>
/// <param name="InterfaceImplemented">
/// Whether the adapter implements the optional interface paired with the flag (e.g. <c>IChannelTypingAdapter</c>).
/// </param>
/// <remarks>
/// The suite enforces <c>CapabilityFlag == InterfaceImplemented</c> so declarations cannot drift from implementations.
/// The OpenClaw lesson from RFC §8.1 is that silent drift between capability declarations and their optional interfaces
/// causes bot code to call into missing surfaces at runtime; this parity is how the suite prevents it.
/// </remarks>
public sealed record CapabilityInterfaceParity(string CapabilityName, bool CapabilityFlag, bool InterfaceImplemented);
