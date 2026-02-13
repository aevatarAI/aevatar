// ─────────────────────────────────────────────────────────────
// MemoryCacheDeduplicator - memory-cache-based event deduplicator.
// Stores eventId in MemoryCache with 5-minute expiration.
// ─────────────────────────────────────────────────────────────

using Aevatar.Deduplication;
using Microsoft.Extensions.Caching.Memory;

namespace Aevatar;

/// <summary>In-memory cache deduplicator to prevent repeated handling of the same event.</summary>
public sealed class MemoryCacheDeduplicator : IEventDeduplicator
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly TimeSpan _expiration = TimeSpan.FromMinutes(5);

    /// <summary>Attempts to record an event ID. True if first-seen, false if duplicate.</summary>
    /// <param name="eventId">Unique event identifier.</param>
    /// <returns>True for first record, false for duplicate.</returns>
    public Task<bool> TryRecordAsync(string eventId)
    {
        if (_cache.TryGetValue(eventId, out _))
            return Task.FromResult(false); // Duplicate
        _cache.Set(eventId, true, _expiration);
        return Task.FromResult(true); // First time
    }
}
