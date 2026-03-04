// ─────────────────────────────────────────────────────────────
// MemoryCacheDeduplicator - memory-cache-based event deduplicator.
// Stores eventId in MemoryCache with 5-minute expiration.
// ─────────────────────────────────────────────────────────────

using Aevatar.Foundation.Abstractions.Deduplication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Persistence;

/// <summary>In-memory cache deduplicator to prevent repeated handling of the same event.</summary>
public sealed class MemoryCacheDeduplicator : IEventDeduplicator
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly TimeSpan _expiration = TimeSpan.FromMinutes(5);
    private readonly ILogger<MemoryCacheDeduplicator> _logger;

    public MemoryCacheDeduplicator(ILogger<MemoryCacheDeduplicator>? logger = null)
    {
        _logger = logger ?? NullLogger<MemoryCacheDeduplicator>.Instance;
    }

    /// <summary>Attempts to record an event ID. True if first-seen, false if duplicate.</summary>
    /// <param name="eventId">Unique event identifier.</param>
    /// <returns>True for first record, false for duplicate.</returns>
    public Task<bool> TryRecordAsync(string eventId)
    {
        if (_cache.TryGetValue(eventId, out _))
        {
            _logger.LogTrace("Dedup HIT (duplicate): {Key}", eventId);
            return Task.FromResult(false); // Duplicate
        }
        _cache.Set(eventId, true, _expiration);
        _logger.LogTrace("Dedup MISS (first-seen): {Key}", eventId);
        return Task.FromResult(true); // First time
    }
}
