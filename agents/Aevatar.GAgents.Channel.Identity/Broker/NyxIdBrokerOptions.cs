using Aevatar.GAgents.Channel.Identity;

namespace Aevatar.GAgents.Channel.Identity.Broker;

/// <summary>
/// Non-secret behavior configuration for the NyxID broker integration.
/// OAuth authority, configured client_id, and actor-owned HMAC key come from
/// <c>IAevatarOAuthClientProvider</c>.
/// </summary>
public sealed class NyxIdBrokerOptions
{
    public const string InternalApiBaseUrlConfigurationKey = "Aevatar:NyxId:InternalApiBaseUrl";
    public const string ApiBaseUrlConfigurationKey = "Aevatar:NyxId:ApiBaseUrl";
    public const string ResourceServerBaseUrlConfigurationKey = ApiBaseUrlConfigurationKey;

    /// <summary>
    /// Effective NyxID HTTP transport base URL used for server-to-server API calls.
    /// This prefers the internal cluster address and must not be used as an
    /// OAuth issuer, audience, or RFC 8707 resource identity.
    /// </summary>
    public string TransportBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional public NyxID REST endpoint used only when the cluster-local transport
    /// fails before DNS resolution or socket connection completes.
    /// </summary>
    public string? PublicTransportFallbackBaseUrl { get; set; }

    /// <summary>
    /// Canonical public NyxID API base URL used for RFC 8707 resource indicators.
    /// This remains independent from the HTTP transport base URL.
    /// </summary>
    public string ResourceServerBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Deprecated compatibility hook. The effective OAuth scope requested at
    /// <c>/oauth/authorize</c> and configured at NyxID is always
    /// <see cref="AevatarOAuthClientScopes.AuthorizationScope"/> so the broker
    /// cannot drift between registered scopes and requested scopes.
    /// </summary>
    public string Scope { get; set; } = AevatarOAuthClientScopes.AuthorizationScope;

    /// <summary>
    /// NyxID service slug used by the deployment's default LLM route. The Host
    /// composition root must source this from the same setting/default as the
    /// LLM provider. When set, every binding flow grants both the core
    /// <c>aevatar</c> service and this LLM service.
    /// </summary>
    public string? RequiredLlmServiceSlug { get; set; }

    /// <summary>
    /// Additional NyxID service slugs required by capabilities exposed through
    /// this OAuth client. Host composition must source each slug from the same
    /// configuration as its corresponding provider so authorization and runtime
    /// routing cannot drift.
    /// </summary>
    public string[] AdditionalRequiredServiceSlugs { get; set; } = [];

    /// <summary>
    /// Lifetime of the stateless <c>state</c> token. Bounds how long a user
    /// can sit on the OAuth authorize URL before completing login. Maximum
    /// 5 minutes per ADR-0018 §Implementation Notes #1.
    /// </summary>
    public TimeSpan StateTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);
}
