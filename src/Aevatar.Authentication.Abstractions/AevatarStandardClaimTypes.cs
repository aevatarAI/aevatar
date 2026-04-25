namespace Aevatar.Authentication.Abstractions;

/// <summary>
/// Standard claim types used across all Aevatar authentication providers.
/// Provider-specific claims are mapped to these via <see cref="IAevatarClaimsTransformer"/>.
/// </summary>
public static class AevatarStandardClaimTypes
{
    /// <summary>Scope identifier for multi-tenant isolation.</summary>
    public const string ScopeId = "scope_id";

    /// <summary>Service tenant identifier for authenticated service governance/serving APIs.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>Service app identifier for authenticated service governance/serving APIs.</summary>
    public const string AppId = "app_id";

    /// <summary>Service namespace identifier for authenticated service governance/serving APIs.</summary>
    public const string Namespace = "namespace";
}
