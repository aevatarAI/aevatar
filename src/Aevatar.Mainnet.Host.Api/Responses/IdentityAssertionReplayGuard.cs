using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>
/// Single-use guard for NyxID identity-assertion <c>jti</c> values: it lets an assertion be
/// consumed exactly once within its accepted lifetime so a captured assertion cannot be replayed.
/// </summary>
internal interface IIdentityAssertionReplayGuard
{
    /// <summary>
    /// Records first use of <paramref name="jti"/> and returns <see langword="true"/>; returns
    /// <see langword="false"/> if the same <paramref name="jti"/> was already consumed and its
    /// accepted lifetime (bounded by <paramref name="acceptedUntilUtc"/>) has not yet elapsed.
    /// </summary>
    ValueTask<bool> TryConsumeAsync(
        string jti,
        DateTimeOffset acceptedUntilUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="IIdentityAssertionReplayGuard"/> backed by a time-ordered map of
/// <c>jti → expiry</c>. Entries evict automatically once past their expiry, so memory stays
/// bounded by the number of assertions currently within their (short) lifetime.
/// </summary>
/// <remarks>
/// This node-local implementation is restricted to Development/test composition. Mainnet
/// production composition uses <see cref="DistributedIdentityAssertionReplayGuard"/>.
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

    public ValueTask<bool> TryConsumeAsync(
        string jti,
        DateTimeOffset acceptedUntilUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                    return ValueTask.FromResult(false);
            }

            // Bound retention by the validator's accepted-lifetime boundary. A non-positive
            // window is rejected upstream by lifetime validation, but if it reaches here we
            // still record it so an immediate duplicate is caught.
            var retainUntil = acceptedUntilUtc > now ? acceptedUntilUtc : now;
            _seenExpiryByJti[jti] = retainUntil;
            _expiryOrder.Enqueue(jti, retainUntil);
            return ValueTask.FromResult(true);
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

internal interface IIdentityAssertionSingleUseStore
{
    ValueTask<bool> TryAddAsync(
        string key,
        TimeSpan retention,
        CancellationToken cancellationToken = default);
}

internal sealed class GarnetIdentityAssertionSingleUseStore : IIdentityAssertionSingleUseStore
{
    private readonly IDatabase _database;

    public GarnetIdentityAssertionSingleUseStore(IConnectionMultiplexer connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _database = connection.GetDatabase();
    }

    public async ValueTask<bool> TryAddAsync(
        string key,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var added = await _database.StringSetAsync(
            key,
            RedisValue.EmptyString,
            retention,
            When.NotExists);
        cancellationToken.ThrowIfCancellationRequested();
        return added;
    }
}

/// <summary>
/// Cluster-wide single-use guard backed by Garnet's atomic SET-NX operation and key TTL.
/// Multiple host replicas share the same key namespace, so exactly one request can consume
/// a given assertion <c>jti</c> during its accepted lifetime.
/// </summary>
internal sealed class DistributedIdentityAssertionReplayGuard : IIdentityAssertionReplayGuard
{
    private const string KeyPrefix = "aevatar:mainnet:nyxid-identity-assertion:jti:";
    private static readonly TimeSpan MinimumRetention = TimeSpan.FromMilliseconds(1);

    private readonly IIdentityAssertionSingleUseStore _store;
    private readonly TimeProvider _timeProvider;

    public DistributedIdentityAssertionReplayGuard(
        IIdentityAssertionSingleUseStore store,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValueTask<bool> TryConsumeAsync(
        string jti,
        DateTimeOffset acceptedUntilUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jti))
            throw new ArgumentException("jti must be a non-empty value.", nameof(jti));

        var retention = acceptedUntilUtc - _timeProvider.GetUtcNow();
        if (retention < MinimumRetention)
            retention = MinimumRetention;

        return _store.TryAddAsync(BuildKey(jti), retention, cancellationToken);
    }

    private static string BuildKey(string jti)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(jti));
        return KeyPrefix + Convert.ToHexStringLower(digest);
    }
}
