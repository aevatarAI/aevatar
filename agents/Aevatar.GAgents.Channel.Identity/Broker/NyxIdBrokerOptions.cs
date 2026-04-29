namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Configuration for <see cref="NyxIdRemoteCapabilityBroker"/>: NyxID base URL,
/// OAuth client credentials, redirect URI, and the HMAC key used to seal the
/// stateless OAuth <c>state</c> token. ADR-0018 §Implementation Notes #1
/// requires the HMAC key to live behind KMS / config (basal infrastructure
/// secret), not in grain state. Keep the secret-bearing fields out of logs.
/// </summary>
public sealed class NyxIdBrokerOptions
{
    /// <summary>
    /// NyxID base URL (no trailing slash), e.g. <c>https://nyxid.aevatar.ai</c>.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// OAuth <c>client_id</c> registered with NyxID for aevatar's broker
    /// integration. Suggested value: <c>aevatar-channel-binding</c>.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth <c>client_secret</c>. MUST be loaded from KMS / secure config;
    /// never log or persist into grain state.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Aevatar's OAuth redirect URI registered with NyxID. NyxID redirects the
    /// browser here with <c>code</c> + <c>state</c> after user login.
    /// </summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Space-separated OAuth scopes requested at <c>/oauth/authorize</c>.
    /// Must include <c>openid</c> (for OIDC <c>id_token</c> + <c>sub</c>) and
    /// <c>urn:nyxid:scope:broker_binding</c> (which triggers NyxID#549's
    /// broker mode — token endpoint returns <c>binding_id</c> instead of
    /// <c>refresh_token</c>).
    /// </summary>
    public string Scope { get; set; } = "openid urn:nyxid:scope:broker_binding";

    /// <summary>
    /// HMAC secret key used by <see cref="StateTokenCodec"/> to seal the
    /// stateless <c>state</c> token. PKCE verifier travels inside this token
    /// (never in grain state). Rotate by adding a new key version while
    /// accepting the old one for the rotation grace period (ADR-0018
    /// §Implementation Notes #1: grace > <c>exp</c>, e.g. ≥ 10 minutes).
    /// </summary>
    public string StateTokenHmacKey { get; set; } = string.Empty;

    /// <summary>
    /// Identifier (<c>kid</c>) for the active state-token signing key. Used
    /// to support rotation: tokens carry <c>kid</c> in their header so the
    /// verifier can pick the right key.
    /// </summary>
    public string StateTokenKid { get; set; } = "default";

    /// <summary>
    /// Lifetime of the stateless <c>state</c> token. Bounds how long a user
    /// can sit on the OAuth authorize URL before completing login. Maximum
    /// 5 minutes per ADR-0018 §Implementation Notes #1.
    /// </summary>
    public TimeSpan StateTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);
}
