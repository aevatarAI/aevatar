using Aevatar.GAgents.Channel.Identity;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Non-secret static defaults for the NyxID broker integration. Authority,
/// client_id, and HMAC key all come from <c>IAevatarOAuthClientProvider</c>
/// at runtime; this options object retains only values that don't change
/// across deployments. No appsettings binding required (the cluster-
/// singleton actor seeds everything).
/// </summary>
public sealed class NyxIdBrokerOptions
{
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
