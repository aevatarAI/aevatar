using System.Threading;
using System.Threading.Tasks;

namespace Aevatar.Authentication.Hosting;

/// <summary>
/// Replay defense for DPoP proof <c>jti</c> values (RFC 9449 §11.1). A proof must be
/// accepted at most once within its freshness window; a repeated <c>jti</c> is a replay.
/// </summary>
public interface IDPoPReplayGuard
{
    /// <summary>
    /// Registers a proof <c>jti</c> as seen. Returns <see langword="true"/> when the
    /// <paramref name="jti"/> is fresh (first use) and <see langword="false"/> when it has
    /// already been seen within the retention window (a replay).
    /// </summary>
    ValueTask<bool> TryRegisterAsync(string jti, CancellationToken cancellationToken = default);
}

/// <summary>
/// No-op default: treats every <c>jti</c> as fresh. Presence (not replay) of <c>jti</c> is
/// still enforced by <see cref="DPoPProofValidator"/>; this default deliberately does NOT
/// dedupe so the feature stays drop-in when no distributed store is wired.
/// <para>
/// A real deployment should replace this with a shared, TTL-bounded jti cache — cluster-5's
/// jti cache is the intended backing store — registered against <see cref="IDPoPReplayGuard"/>.
/// </para>
/// </summary>
public sealed class NoOpDPoPReplayGuard : IDPoPReplayGuard
{
    public ValueTask<bool> TryRegisterAsync(string jti, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);
}
