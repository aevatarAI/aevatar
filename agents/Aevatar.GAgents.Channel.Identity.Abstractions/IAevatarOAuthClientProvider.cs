namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Read seam exposing the aevatar host's own OAuth client provisioning state
/// (client_id, HMAC keys, broker capability) to the broker / state-token
/// codec / OAuth callback. Backed by a cluster-singleton actor so every silo
/// sees the same registration without per-host config drift. Production
/// wiring is gated on the bootstrap service having completed at least one
/// DCR call.
/// </summary>
public interface IAevatarOAuthClientProvider
{
    /// <summary>
    /// Returns the cluster-shared OAuth client snapshot, awaiting the
    /// bootstrap actor activation if necessary. Throws
    /// <see cref="AevatarOAuthClientNotProvisionedException"/> when the
    /// cluster has never successfully called NyxID DCR (production
    /// deployments treat this as a startup-time failure, not a per-request
    /// fault). The snapshot includes the active HMAC key plus (optionally)
    /// the demoted previous key kept inside the rotation grace window so
    /// in-flight state tokens signed by the prior key still verify (PR
    /// #521 review v4-pro on kid rotation).
    /// </summary>
    Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Immutable snapshot of the cluster-singleton OAuth client state.
/// </summary>
/// <remarks>
/// Carries both the current HMAC material and (optionally) the demoted
/// previous material so verifiers (state-token codec, broker revocation
/// webhook) can accept tokens signed by either key during the rotation
/// grace window. <see cref="PreviousHmacKey"/> is non-null only when a
/// rotation has happened and <see cref="PreviousHmacDemotedAt"/> is within
/// the configured state-token lifetime.
/// </remarks>
public sealed record AevatarOAuthClientSnapshot(
    string ClientId,
    DateTimeOffset ClientIdIssuedAt,
    string HmacKid,
    byte[] HmacKey,
    DateTimeOffset HmacKeyRotatedAt,
    string NyxIdAuthority,
    bool BrokerCapabilityObserved,
    DateTimeOffset? BrokerCapabilityObservedAt,
    string? PreviousHmacKid = null,
    byte[]? PreviousHmacKey = null,
    DateTimeOffset? PreviousHmacDemotedAt = null,
    string? RedirectUri = null,
    string? OauthScope = null);

/// <summary>
/// Thrown when an OAuth flow tries to use the cluster client before the
/// bootstrap service has registered against NyxID. Surfaces actionable ops
/// guidance: "wait for the host to finish startup, or check the bootstrap
/// service logs for a NyxID DCR error".
/// </summary>
public sealed class AevatarOAuthClientNotProvisionedException : Exception
{
    /// <summary>
    /// Creates a new <see cref="AevatarOAuthClientNotProvisionedException"/>.
    /// </summary>
    public AevatarOAuthClientNotProvisionedException(string? message = null)
        : base(message ?? "Aevatar OAuth client has not been provisioned at NyxID. Wait for the cluster bootstrap service to complete, or inspect its logs for a registration error.")
    {
    }
}
