using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Authentication.ScopeServiceTokens;

public sealed class ScopeServiceTokenIssuer(
    IScopeServiceTokenKeyProvider keyProvider,
    IOptions<ScopeServiceTokenOptions> options) : IScopeServiceTokenIssuer
{
    public ScopeServiceToken Issue(ScopeServiceTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scopeId = NormalizeRequired(request.ScopeId, nameof(request.ScopeId));
        var serviceId = NormalizeRequired(request.ServiceId, nameof(request.ServiceId));
        var serviceKey = NormalizeRequired(request.ServiceKey, nameof(request.ServiceKey));
        var now = DateTimeOffset.UtcNow;
        var expires = now + ResolveLifetime(options.Value);
        var signingKey = keyProvider.CurrentSigningKey;

        var claims = new List<Claim>
        {
            new("scope_id", scopeId),
            new("aevatar.scope_service", "true"),
            new("aevatar.service_id", serviceId),
            new("aevatar.service_key", serviceKey),
        };

        AddOptionalClaim(claims, "aevatar.tenant_id", request.TenantId);
        AddOptionalClaim(claims, "aevatar.app_id", request.AppId);
        AddOptionalClaim(claims, "aevatar.namespace", request.Namespace);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = keyProvider.Issuer,
            Audience = keyProvider.Audience,
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(signingKey.SigningKey, signingKey.Algorithm),
        };

        var jwt = new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
        return new ScopeServiceToken(jwt, signingKey.Kid, expires);
    }

    private static TimeSpan ResolveLifetime(ScopeServiceTokenOptions options)
    {
        var minutes = options.TokenLifetimeMinutes <= 0 ? 60 : options.TokenLifetimeMinutes;
        return TimeSpan.FromMinutes(minutes);
    }

    private static void AddOptionalClaim(List<Claim> claims, string type, string? value)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
            claims.Add(new Claim(type, normalized));
    }

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{name} is required.");

        return normalized;
    }
}
