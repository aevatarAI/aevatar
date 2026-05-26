namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Adapter-supplied probe that drives and observes the typing-indicator surface for RFC §8.1 typing conformance tests.
/// </summary>
/// <remarks>
/// <para>
/// Typing behavior is not part of <see cref="IChannelOutboundPort"/>; when a channel supports it, the adapter exposes
/// an adapter-specific entry point and runs background keepalive timers. The conformance suite delegates both the
/// scenario drive and the observation to this probe so the base class does not need to know the adapter's typing API.
/// </para>
/// <para>
/// The probe is required when <see cref="ChannelCapabilities.SupportsTyping"/> is <see langword="true"/>. Each typing
/// conformance test fails when the capability is claimed but no probe is supplied.
/// </para>
/// </remarks>
public abstract class TypingProbe
{
    /// <summary>
    /// Starts typing, observes the adapter for one keepalive cycle, and returns whether at least one keepalive event
    /// fired within the adapter's declared keepalive interval.
    /// </summary>
    public abstract Task<bool> KeepaliveFiresWithinIntervalAsync(CancellationToken ct = default);

    /// <summary>
    /// Starts typing, waits past the declared TTL without calling stop, and returns whether the adapter auto-stopped
    /// typing once the TTL elapsed.
    /// </summary>
    public abstract Task<bool> AutoStopsAfterTtlAsync(CancellationToken ct = default);

    /// <summary>
    /// Induces two consecutive keepalive failures and returns whether the adapter tripped its circuit breaker and
    /// stopped sending further keepalives.
    /// </summary>
    public abstract Task<bool> CircuitBreakerTripsAfterConsecutiveFailuresAsync(CancellationToken ct = default);

    /// <summary>
    /// Calls start and stop multiple times in sequence and returns whether repeated transitions are safe (no duplicate
    /// platform calls, no lingering keepalive after stop, no exceptions).
    /// </summary>
    public abstract Task<bool> StartStopIsIdempotentAsync(CancellationToken ct = default);
}
