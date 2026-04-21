namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// One capability-flag ↔ optional-interface parity rule supplied by an adapter.
/// </summary>
/// <param name="CapabilityName">
/// Descriptive name of the capability (e.g. <c>"SupportsTyping"</c>). Surfaced in assertion failure messages.
/// </param>
/// <param name="CapabilityFlag">The current value of the capability flag on the adapter's <c>ChannelCapabilities</c>.</param>
/// <param name="OptionalInterface">
/// The optional interface paired with the flag (e.g. <c>typeof(IChannelTypingAdapter)</c>). The suite uses
/// <c>ChannelAdapterConformanceTests.AdapterImplements(adapter, OptionalInterface)</c> to read whether the adapter
/// actually implements it at runtime, so adapter authors cannot fake the parity by supplying the wrong bool.
/// </param>
/// <remarks>
/// The suite enforces <c>CapabilityFlag == AdapterImplements(adapter, OptionalInterface)</c> so declarations cannot
/// drift from implementations. The OpenClaw lesson from RFC §8.1 is that silent drift between capability declarations
/// and their optional interfaces causes bot code to call into missing surfaces at runtime; this parity is how the
/// suite prevents it.
/// </remarks>
public sealed record CapabilityInterfaceParity(string CapabilityName, bool CapabilityFlag, Type OptionalInterface);
