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
    /// Space-separated OAuth scopes requested at <c>/oauth/authorize</c>.
    /// MUST include <c>openid</c> (for OIDC <c>id_token</c> + <c>sub</c>) and
    /// <c>urn:nyxid:scope:broker_binding</c> (which tells NyxID#549 to
    /// return <c>binding_id</c> instead of <c>refresh_token</c> on
    /// authorization-code exchange).
    /// </summary>
    public string Scope { get; set; } = "openid urn:nyxid:scope:broker_binding";

    /// <summary>
    /// Lifetime of the stateless <c>state</c> token. Bounds how long a user
    /// can sit on the OAuth authorize URL before completing login. Maximum
    /// 5 minutes per ADR-0018 §Implementation Notes #1.
    /// </summary>
    public TimeSpan StateTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);
}
