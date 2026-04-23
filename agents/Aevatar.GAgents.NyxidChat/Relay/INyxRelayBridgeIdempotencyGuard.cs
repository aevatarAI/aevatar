using System.Collections.Concurrent;

namespace Aevatar.GAgents.NyxidChat.Relay;

/// <summary>
/// Atomic, boundary-level first-writer-wins guard for Nyx relay bridge callbacks.
///
/// Unlike <c>IEventDeduplicator</c>, whose <c>MemoryCacheDeduplicator</c> implementation
/// performs a non-atomic <c>TryGetValue</c> + <c>Set</c> pair (safe inside a serialized
/// grain mailbox, unsafe at a concurrent HTTP boundary), this guard uses
/// <c>ConcurrentDictionary.TryAdd</c> / <c>TryUpdate</c> so that two simultaneous
/// deliveries of the same webhook cannot both claim first-seen within a single process.
///
/// The guard is per-process: Nyx typically routes retries of the same callback to the
/// same Aevatar host, so node-local atomicity covers the observed retry pattern. Cross-
/// node replays would still need an upstream idempotency layer, but that is outside the
/// scope of this bridge.
/// </summary>
public interface INyxRelayBridgeIdempotencyGuard
{
    /// <summary>
    /// Atomically claim <paramref name="key"/>. Returns true only for the first caller to
    /// observe the key as absent (or expired); every concurrent caller sees false.
    /// </summary>
    bool TryClaim(string key);
}

internal sealed class NyxRelayBridgeIdempotencyGuard : INyxRelayBridgeIdempotencyGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _claims = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;

    public NyxRelayBridgeIdempotencyGuard()
        : this(TimeSpan.FromMinutes(10), TimeProvider.System)
    {
    }

    internal NyxRelayBridgeIdempotencyGuard(TimeSpan ttl, TimeProvider timeProvider)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        _ttl = ttl;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool TryClaim(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = _timeProvider.GetUtcNow();
        var newExpiry = now + _ttl;

        // Hot path: key absent. TryAdd is atomic — only one concurrent caller succeeds.
        if (_claims.TryAdd(key, newExpiry))
            return true;

        // Cold path: key present. Only replace if expired; do so atomically.
        while (_claims.TryGetValue(key, out var existing))
        {
            if (existing > now)
                return false;

            // Expired. TryUpdate succeeds only if the current value still equals `existing`,
            // so another concurrent claimant cannot also succeed.
            if (_claims.TryUpdate(key, newExpiry, existing))
                return true;

            // Someone else mutated the entry between our read and update; loop and retry.
        }

        // Entry vanished between TryAdd and TryGetValue (concurrent eviction). Try fresh add.
        return _claims.TryAdd(key, newExpiry);
    }
}
