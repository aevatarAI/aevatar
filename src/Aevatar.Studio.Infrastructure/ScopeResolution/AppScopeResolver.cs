using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Infrastructure.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Studio.Infrastructure.ScopeResolution;

/// <summary>
/// Resolves the current scope from an authenticated principal.
/// <para>
/// The authoritative source is the <c>scope_id</c> claim. Synthesising <c>scope_id</c> from
/// provider-specific claims (NyxID's <c>uid</c>/<c>sub</c>, generic <c>*_id</c>, etc.) belongs
/// to the authentication provider's <see cref="Aevatar.Authentication.Abstractions.IAevatarClaimsTransformer"/>,
/// which runs in <c>AevatarClaimsTransformation</c> before any request handler sees the principal.
/// Duplicating the mapping here silently diverged from the provider's contract in the past
/// (see issue #251 Q-04 / Q-08), so this resolver now only reads the canonical claim.
/// </para>
/// <para>
/// When authentication is disabled (<c>Cli:App:NyxId:Enabled = false</c>) we fall back to
/// explicit scope headers so local developer workflows keep working without a token.
/// Configuration / environment fallbacks remain available for CLI contexts where no request
/// is in flight.
/// </para>
/// </summary>
public sealed class DefaultAppScopeResolver : IAppScopeResolver
{
    private const string ScopeIdClaimType = "scope_id";

    private static readonly string[] ScopeHeaders =
    [
        "X-Aevatar-Scope-Id",
        "X-Scope-Id",
    ];

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly bool _nyxIdAuthEnabled;

    public DefaultAppScopeResolver(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _nyxIdAuthEnabled = _configuration.GetSection(NyxIdAppAuthOptions.SectionName).Get<NyxIdAppAuthOptions>()?.Enabled ?? true;
    }

    public AppScopeContext? Resolve(HttpContext? httpContext = null)
    {
        var context = httpContext ?? _httpContextAccessor.HttpContext;
        if (context != null)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                var scopeIdValue = user.FindFirst(ScopeIdClaimType)?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(scopeIdValue))
                    return new AppScopeContext(scopeIdValue, $"claim:{ScopeIdClaimType}");
            }

            if (_nyxIdAuthEnabled)
                return null;

            foreach (var headerName in ScopeHeaders)
            {
                var headerValue = context.Request.Headers[headerName].FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(headerValue))
                    return new AppScopeContext(headerValue, $"header:{headerName}");
            }
        }

        var configuredScopeId = _configuration["Cli:App:ScopeId"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredScopeId))
            return new AppScopeContext(configuredScopeId, "config:Cli:App:ScopeId");

        var environmentScopeId = _configuration["AEVATAR_SCOPE_ID"]?.Trim();
        if (!string.IsNullOrWhiteSpace(environmentScopeId))
            return new AppScopeContext(environmentScopeId, "env:AEVATAR_SCOPE_ID");

        return null;
    }

    public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null)
    {
        var context = httpContext ?? _httpContextAccessor.HttpContext;
        if (context?.User?.Identity?.IsAuthenticated != true)
            return false;

        var scopeIdValue = context.User.FindFirst(ScopeIdClaimType)?.Value?.Trim();
        return string.IsNullOrWhiteSpace(scopeIdValue);
    }
}
