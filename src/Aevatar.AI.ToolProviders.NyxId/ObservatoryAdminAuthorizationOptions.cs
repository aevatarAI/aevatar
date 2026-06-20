namespace Aevatar.AI.ToolProviders.NyxId;

// 06-20-observatory-admin-cross-scope (G8): tuning for the platform-admin authorizer's per-token cache.
public sealed class ObservatoryAdminAuthorizationOptions
{
    public const string ConfigSection = "Aevatar:Observatory";

    // Short TTL for caching a POSITIVE (elevated) admin decision per token, on this node only. NyxID stays the
    // source of truth; this bounds NyxID round-trips during 3s polling. A demoted admin keeps access for at most
    // this window per node (accepted risk). Denials/errors are never cached. Default 30s.
    public int AdminRoleCacheTtlSeconds { get; set; } = 30;

    // Kill-switch: when false, the observatory admin cross-scope path is disabled (every caller is treated as
    // non-admin) without a redeploy. Default true.
    public bool CrossScopeEnabled { get; set; } = true;
}
