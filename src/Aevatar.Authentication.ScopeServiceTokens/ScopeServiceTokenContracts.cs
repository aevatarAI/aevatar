using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Authentication.ScopeServiceTokens;

public interface IScopeServiceTokenIssuer
{
    ScopeServiceToken Issue(ScopeServiceTokenRequest request);
}

public interface IScopeServiceTokenKeyProvider
{
    string Issuer { get; }

    string? Audience { get; }

    TimeSpan ClockSkew { get; }

    ScopeServiceSigningKey CurrentSigningKey { get; }

    IReadOnlyList<ScopeServiceSigningKey> ValidationKeys { get; }
}

public sealed record ScopeServiceTokenRequest(
    string ScopeId,
    string ServiceId,
    string ServiceKey,
    string? TenantId = null,
    string? AppId = null,
    string? Namespace = null);

public sealed record ScopeServiceToken(
    string AccessToken,
    string Kid,
    DateTimeOffset ExpiresAt);

public sealed record ScopeServiceSigningKey(
    string Kid,
    string Algorithm,
    SecurityKey SigningKey,
    SecurityKey ValidationKey);
