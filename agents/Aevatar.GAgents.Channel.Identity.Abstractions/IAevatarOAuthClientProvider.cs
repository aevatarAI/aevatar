namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>
/// Read seam exposing the aevatar host's configured OAuth client identity plus
/// actor-owned HMAC, callback/scope, and broker-capability state to the broker,
/// state-token codec, and OAuth callback.
/// </summary>
public interface IAevatarOAuthClientProvider
{
    /// <summary>
    /// Returns the cluster-shared OAuth client snapshot. The client id comes
    /// from deployment configuration; actor projection owns the remaining
    /// runtime facts. Throws
    /// <see cref="AevatarOAuthClientNotProvisionedException"/> when the
    /// configured client id or materialized actor facts are unavailable. The
    /// snapshot includes the active HMAC key plus (optionally)
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
    string? OauthScope = null,
    IReadOnlyList<string>? RedirectUris = null);

/// <summary>
/// Thrown when an OAuth flow cannot resolve the configured client identity or
/// its actor-owned runtime facts.
/// </summary>
public sealed class AevatarOAuthClientNotProvisionedException : Exception
{
    /// <summary>
    /// Creates a new <see cref="AevatarOAuthClientNotProvisionedException"/>.
    /// </summary>
    public AevatarOAuthClientNotProvisionedException(string? message = null)
        : base(message ?? "Aevatar OAuth client configuration or actor materialization is unavailable. Check host configuration and bootstrap logs.")
    {
    }
}
