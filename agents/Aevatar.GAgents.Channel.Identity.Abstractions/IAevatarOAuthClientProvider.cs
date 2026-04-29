namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Read seam exposing the aevatar host's own OAuth client provisioning state
/// (client_id, HMAC key, broker capability) to the broker / state-token codec
/// / OAuth callback. Backed by a cluster-singleton actor so every silo sees
/// the same registration without per-host config drift. Production wiring is
/// gated on the bootstrap service having completed at least one DCR call.
/// </summary>
public interface IAevatarOAuthClientProvider
{
    /// <summary>
    /// Returns the cluster-shared OAuth client snapshot, awaiting the
    /// bootstrap actor activation if necessary. Throws
    /// <see cref="AevatarOAuthClientNotProvisionedException"/> when the
    /// cluster has never successfully called NyxID DCR (production
    /// deployments treat this as a startup-time failure, not a per-request
    /// fault).
    /// </summary>
    Task<AevatarOAuthClientSnapshot> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Immutable snapshot of the cluster-singleton OAuth client state.
/// </summary>
public sealed record AevatarOAuthClientSnapshot(
    string ClientId,
    DateTimeOffset ClientIdIssuedAt,
    byte[] HmacKey,
    DateTimeOffset HmacKeyRotatedAt,
    string NyxIdAuthority,
    bool BrokerCapabilityObserved,
    DateTimeOffset? BrokerCapabilityObservedAt);

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
