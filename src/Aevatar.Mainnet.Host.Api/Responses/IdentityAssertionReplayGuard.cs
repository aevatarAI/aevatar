namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>
/// Single-use guard for NyxID identity-assertion <c>jti</c> values: it lets an assertion be
/// consumed exactly once within its own lifetime so a captured assertion cannot be replayed.
/// </summary>
internal interface IIdentityAssertionReplayGuard
{
    /// <summary>
    /// Records first use of <paramref name="jti"/> and returns <see langword="true"/>; returns
    /// <see langword="false"/> if the same <paramref name="jti"/> was already consumed and its
    /// lifetime (bounded by <paramref name="expiresUtc"/>) has not yet elapsed.
    /// </summary>
    bool TryConsume(string jti, DateTimeOffset expiresUtc);
}

/// <summary>
/// In-memory <see cref="IIdentityAssertionReplayGuard"/> backed by a time-ordered map of
/// <c>jti → expiry</c>. Entries evict automatically once past their expiry, so memory stays
/// bounded by the number of assertions currently within their (short) lifetime.
/// </summary>
/// <remarks>
/// This node-local implementation only detects replays observed by the same host instance. A
/// distributed backing (e.g. a Garnet / Redis-style shared store with per-key TTL keyed by
/// <c>jti</c>) can replace this default to make the guard cluster-wide without changing the
/// <see cref="IIdentityAssertionReplayGuard"/> contract or its callers.
/// </remarks>
internal sealed class InMemoryIdentityAssertionReplayGuard : IIdentityAssertionReplayGuard
{
    // Time-ordered by expiry so eviction is a cheap front-of-queue scan; the dictionary is the
    // authoritative "seen jti" set. Both are mutated only under _gate — this guard is a shared
    // singleton hit concurrently by request threads, and the seen-set is process-local runtime
    // state (not a cross-node fact source), so a lock here is the correct serialization, not a
    // concurrency patch over an actor.
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _seenExpiryByJti = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, DateTimeOffset> _expiryOrder = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryIdentityAssertionReplayGuard(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool TryConsume(string jti, DateTimeOffset expiresUtc)
    {
        if (string.IsNullOrWhiteSpace(jti))
            throw new ArgumentException("jti must be a non-empty value.", nameof(jti));

        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            EvictExpired(now);

            if (_seenExpiryByJti.TryGetValue(jti, out var existingExpiry))
            {
                // Seen and still live -> replay. (Anything already expired was evicted above,
                // so a surviving entry is unambiguously an in-lifetime duplicate.)
                if (existingExpiry > now)
                    return false;
            }

            // Bound the retention window by the assertion's own expiry; a non-positive window
            // (already-expired assertion) is rejected upstream by lifetime validation, but if it
            // reaches here we still record it so an immediate duplicate is caught.
            var retainUntil = expiresUtc > now ? expiresUtc : now;
            _seenExpiryByJti[jti] = retainUntil;
            _expiryOrder.Enqueue(jti, retainUntil);
            return true;
        }
    }

    private void EvictExpired(DateTimeOffset now)
    {
        while (_expiryOrder.TryPeek(out var jti, out var expiry) && expiry <= now)
        {
            _expiryOrder.Dequeue();
            // A jti can appear multiple times in the ordering queue (re-recorded after an earlier
            // eviction). Only drop the dictionary entry when its authoritative expiry matches the
            // one we are evicting, so a newer live record is not removed by a stale queue slot.
            if (_seenExpiryByJti.TryGetValue(jti, out var current) && current == expiry)
                _seenExpiryByJti.Remove(jti);
        }
    }
}
