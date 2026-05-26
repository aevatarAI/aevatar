namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Per-shard single-activation grain contract used by gateway-based transports (Discord, WeChat
/// wechaty, etc.) to serialize gateway session ownership via a fencing lease.
/// </summary>
/// <remarks>
/// <para>
/// This interface is part of issue #258 deliverables but the Discord gateway + lease implementation
/// is explicitly deferred to a later RFC sub-issue. Adapters that do not need gateway fencing
/// (Lark webhook, Telegram webhook) are not required to depend on this contract.
/// </para>
/// <para>
/// Fencing semantics: one shard-id maps to at most one live activation. Lease holders must renew
/// before TTL expiry, otherwise the activation releases and another caller may acquire. The
/// <see cref="LeaseToken"/> includes an <c>epoch</c> monotonic counter so late writes from a lost
/// lease can be rejected by downstream stores.
/// </para>
/// </remarks>
public interface IShardLeaderGrain
{
    /// <summary>
    /// Acquires the lease for <paramref name="shardId"/>. Throws when another owner holds an
    /// unexpired lease. Returns a fresh <see cref="LeaseToken"/> with a bumped epoch.
    /// </summary>
    Task<LeaseToken> AcquireLeaseAsync(string shardId, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Renews the existing lease. Returns <see langword="false"/> when the token's epoch no longer
    /// matches the current activation's view, so the caller must stop writing under that lease.
    /// </summary>
    Task<bool> RenewLeaseAsync(LeaseToken token, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Releases the lease if the caller still owns the current epoch.
    /// </summary>
    Task ReleaseLeaseAsync(LeaseToken token, CancellationToken ct);
}
