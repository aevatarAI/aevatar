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
/// Disabled-feature placeholder that treats every <c>jti</c> as fresh.
/// <para>
/// <see cref="DPoPReplayGuardStartupValidator"/> prevents a host from starting with DPoP
/// enabled while this implementation is registered.
/// </para>
/// </summary>
public sealed class NoOpDPoPReplayGuard : IDPoPReplayGuard
{
    public ValueTask<bool> TryRegisterAsync(string jti, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);
}
