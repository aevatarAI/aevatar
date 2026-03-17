using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Aevatar.Tools.Cli.Hosting;

public sealed record AppScopeContext(string ScopeId, string Source);

public interface IAppScopeResolver
{
    AppScopeContext? Resolve(HttpContext? httpContext = null);
}

public sealed class DefaultAppScopeResolver : IAppScopeResolver
{
    private static readonly HashSet<string> IgnoredGenericIdClaimTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "client_id",
        "session_id",
        "sid",
    };

    private static readonly string[] ScopeHeaders =
    [
        "X-Aevatar-Scope-Id",
        "X-Scope-Id",
    ];

    private static readonly string[] ScopeClaimTypes =
    [
        "scope_id",
        "uid",
        "sub",
        ClaimTypes.NameIdentifier,
    ];

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;

    public DefaultAppScopeResolver(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public AppScopeContext? Resolve(HttpContext? httpContext = null)
    {
        var context = httpContext ?? _httpContextAccessor.HttpContext;
        if (context != null)
        {
            var user = context.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                foreach (var claimType in ScopeClaimTypes)
                {
                    var claimValue = user.FindFirst(claimType)?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(claimValue))
                        return new AppScopeContext(claimValue, $"claim:{claimType}");
                }

                var genericIdClaim = user.Claims.FirstOrDefault(claim =>
                    claim.Type.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
                    !IgnoredGenericIdClaimTypes.Contains(claim.Type) &&
                    !string.IsNullOrWhiteSpace(claim.Value));
                if (genericIdClaim != null)
                    return new AppScopeContext(genericIdClaim.Value.Trim(), $"claim:{genericIdClaim.Type}");
            }

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
}
