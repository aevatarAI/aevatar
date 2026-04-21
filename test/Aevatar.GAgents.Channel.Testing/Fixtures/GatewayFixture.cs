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
    /// Publishes one synthetic gateway event so the adapter surfaces it as a <see cref="ChatActivity"/>.
    /// </summary>
    public abstract Task PublishEventAsync(InboundActivitySeed seed, CancellationToken ct = default);

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
public sealed record GatewaySessionSnapshot(string ResumeTokenAtStart, bool IsResumed);
