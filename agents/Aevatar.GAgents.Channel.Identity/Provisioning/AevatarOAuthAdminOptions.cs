namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Operator credentials for the cluster-singleton OAuth client admin
/// surface. Currently only protects the rebuild endpoint
/// (<c>POST /api/oauth/aevatar-client/rebuild</c>) — see issue #549 for the
/// production wedge that motivated it.
/// </summary>
/// <remarks>
/// Bound from configuration section <c>ChannelIdentity:Admin</c>. When
/// <see cref="RebuildToken"/> is empty the rebuild endpoint refuses to
/// run (503), so a misconfigured cluster is fail-secure rather than
/// fail-open. Production deploys set the token via env var
/// <c>ChannelIdentity__Admin__RebuildToken</c>; tests/dev clusters may
/// leave it unset and the endpoint stays disabled.
/// </remarks>
public sealed class AevatarOAuthAdminOptions
{
    /// <summary>
    /// Configuration section name under <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
    /// </summary>
    public const string SectionName = "ChannelIdentity:Admin";

    /// <summary>
    /// Header callers send the rebuild token in. Constant-time compared to
    /// <see cref="RebuildToken"/>; mismatch returns 401.
    /// </summary>
    public const string RebuildTokenHeader = "X-Aevatar-Admin-Token";

    /// <summary>
    /// Shared secret required on the rebuild endpoint. Empty disables the
    /// endpoint entirely (fail-secure default).
    /// </summary>
    public string RebuildToken { get; set; } = string.Empty;
}
