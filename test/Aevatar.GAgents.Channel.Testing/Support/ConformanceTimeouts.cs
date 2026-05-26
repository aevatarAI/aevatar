namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Shared timing defaults used by the conformance and fault suites.
/// </summary>
/// <remarks>
/// Suite-level timeouts are intentionally generous so CI variance in slow fixtures does not flip conformance results.
/// Concrete adapter tests may shorten them in a derived class by overriding the <c>Timeouts</c> property.
/// </remarks>
public sealed class ConformanceTimeouts
{
    /// <summary>
    /// Gets the default timeout for observing one inbound activity produced in response to a synthetic webhook dispatch.
    /// </summary>
    public TimeSpan InboundDispatch { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the default timeout for observing one outbound send to settle.
    /// </summary>
    public TimeSpan OutboundSend { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the default timeout applied to an adapter lifecycle transition (initialize / start / stop).
    /// </summary>
    public TimeSpan Lifecycle { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the default timeout used when waiting for a streaming handle to settle.
    /// </summary>
    public TimeSpan Streaming { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the suite-wide default timeouts.
    /// </summary>
    public static ConformanceTimeouts Default { get; } = new();
}
