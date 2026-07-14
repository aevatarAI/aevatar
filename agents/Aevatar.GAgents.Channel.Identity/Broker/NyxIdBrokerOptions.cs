using Aevatar.GAgents.Channel.Identity;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Non-secret static configuration for the NyxID broker integration. OAuth
/// authority, client_id, and HMAC key come from
/// <c>IAevatarOAuthClientProvider</c>; the canonical resource-server base is
/// deployment configuration because it is not an OAuth-client fact.
/// </summary>
public sealed class NyxIdBrokerOptions
{
    public const string ResourceServerBaseUrlConfigurationKey = "Aevatar:NyxId:ApiBaseUrl";

    /// <summary>
    /// Canonical NyxID API base used to construct RFC 8707 resource URIs.
    /// This is deliberately separate from the browser-facing OAuth authority.
    /// </summary>
    public string ResourceServerBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Deprecated compatibility hook. The effective OAuth scope requested at
    /// <c>/oauth/authorize</c> and registered through DCR is always
    /// <see cref="AevatarOAuthClientScopes.AuthorizationScope"/> so the broker
    /// cannot drift between registered scopes and requested scopes.
    /// </summary>
    public string Scope { get; set; } = AevatarOAuthClientScopes.AuthorizationScope;

    /// <summary>
    /// Lifetime of the stateless <c>state</c> token. Bounds how long a user
    /// can sit on the OAuth authorize URL before completing login. Maximum
    /// 5 minutes per ADR-0018 §Implementation Notes #1.
    /// </summary>
    public TimeSpan StateTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);
}
