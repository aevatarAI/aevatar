using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Testing;

/// <summary>
/// Drives one gateway-style adapter under test.
/// </summary>
/// <remarks>
/// Conformance and fault tests use this fixture to simulate RESUME / IDENTIFY handshakes and inject gateway lifecycle
/// events without requiring a live platform socket.
/// </remarks>
public abstract class GatewayFixture
{
    /// <summary>
    /// Starts a fresh gateway session and returns the seed state observed by the adapter.
    /// </summary>
    public abstract Task<GatewaySessionSnapshot> StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Instructs the fixture to invalidate the current RESUME token so the adapter must fall back to IDENTIFY.
    /// </summary>
    public abstract Task InvalidateResumeTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops the current gateway connection without letting the adapter run its pre-stop persistence hook.
    /// </summary>
    /// <remarks>
    /// Fault tests use this to model pod eviction where preStop never ran, leaving the adapter with a stale last-seen seq.
    /// </remarks>
    public abstract Task DropConnectionWithoutPreStopAsync(CancellationToken ct = default);

    /// <summary>
    /// Publishes one synthetic gateway event and returns the normalized activity the adapter emitted in response.
    /// </summary>
    /// <remarks>
    /// Conformance tests consume the returned activity directly, so gateway-only adapters do not need any additional
    /// override to satisfy the inbound test matrix.
    /// </remarks>
    public abstract Task<ChatActivity> PublishEventAsync(InboundActivitySeed seed, CancellationToken ct = default);

    /// <summary>
    /// Replays the last synthetic gateway event so the adapter observes a retry of the same payload.
    /// </summary>
    /// <remarks>
    /// Used by deduplication tests. Implementations must replay the exact event (payload, sequence number, dedup key)
    /// from the previous <see cref="PublishEventAsync"/> call so the adapter cannot distinguish the retry from the
    /// original. Return <see langword="null"/> when no prior event exists; tests handle that as not-applicable.
    /// </remarks>
    public abstract Task<ChatActivity?> ReplayLastEventAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the sequence number the fixture currently considers authoritative on the platform side.
    /// </summary>
    public abstract long AuthoritativeSequenceNumber { get; }
}

/// <summary>
/// Snapshot of the gateway session state the fixture records after a handshake.
/// </summary>
/// <param name="ResumeTokenAtStart">The RESUME token the adapter holds after connecting.</param>
/// <param name="IsResumed">Whether the fixture satisfied the handshake as a RESUME rather than IDENTIFY.</param>
/// <param name="EventGapDetected">
/// Whether the adapter detected an event-sequence gap on this reconnect — for example, because a previous pod
/// dropped without persisting its latest last-seen seq via pre-stop. The assertion for
/// <c>Gateway_PreStopMissing_DetectsEventGap</c> reads this flag.
/// </param>
public sealed record GatewaySessionSnapshot(string ResumeTokenAtStart, bool IsResumed, bool EventGapDetected = false);
