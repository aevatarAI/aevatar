namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// Abstraction for caching NyxID service discovery results.
/// Implementations can be in-memory (dev/demo) or distributed (production).
/// </summary>
public interface IServiceDiscoveryCache
{
    /// <summary>
    /// Get cached service slugs for a token. Returns null on cache miss.
    /// </summary>
    HashSet<string>? GetSlugs(string tokenHash);

    /// <summary>
    /// Store service slugs for a token with TTL-based expiry.
    /// </summary>
    void SetSlugs(string tokenHash, HashSet<string> slugs);
}

/// <summary>
/// In-memory TTL-based cache for service discovery results.
/// Suitable for dev/demo. Replace with distributed cache for production.
/// </summary>
public sealed class InMemoryServiceDiscoveryCache : IServiceDiscoveryCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, (HashSet<string> Slugs, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly object _lock = new();

    public HashSet<string>? GetSlugs(string tokenHash)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(tokenHash, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
                return entry.Slugs;

            // Expired or missing — evict
            _cache.Remove(tokenHash);
            return null;
        }
    }

    public void SetSlugs(string tokenHash, HashSet<string> slugs)
    {
        lock (_lock)
        {
            _cache[tokenHash] = (slugs, DateTimeOffset.UtcNow.Add(DefaultTtl));
        }
    }
}
