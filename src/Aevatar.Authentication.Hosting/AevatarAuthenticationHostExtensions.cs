using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Authentication.Hosting;

public static class AevatarAuthenticationHostExtensions
{
    /// <summary>
    /// Registers JWT Bearer authentication if <c>Aevatar:Authentication:Enabled</c> is true.
    /// Provider-agnostic: uses OIDC discovery from the configured Authority.
    /// Requires an <see cref="IAevatarClaimsTransformer"/> to be registered by the provider package.
    /// </summary>
    public static WebApplicationBuilder AddAevatarAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration
            .GetSection(AevatarAuthenticationOptions.SectionName)
            .Get<AevatarAuthenticationOptions>();

        if (options?.Enabled != true)
            return builder;

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

                jwt.TokenValidationParameters.ValidAudience = options.Audience;
                jwt.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience);
            });

        // When authentication is enabled, endpoints default to requiring an authenticated caller.
        // Public endpoints must opt out with [AllowAnonymous] / .AllowAnonymous().
        builder.Services.AddAuthorization(authorization =>
        {
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build();
        });
        builder.Services.AddTransient<IClaimsTransformation, AevatarClaimsTransformation>();

        return builder;
    }
}

/// <summary>
/// Bridges ASP.NET Core's <see cref="IClaimsTransformation"/> to
/// <see cref="IAevatarClaimsTransformer"/> implementations registered by auth providers.
/// </summary>
internal sealed class AevatarClaimsTransformation : IClaimsTransformation
{
    private readonly IEnumerable<IAevatarClaimsTransformer> _transformers;

    public AevatarClaimsTransformation(IEnumerable<IAevatarClaimsTransformer> transformers)
    {
        _transformers = transformers;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        foreach (var transformer in _transformers)
        {
            var additionalClaims = transformer.TransformClaims(principal);
            foreach (var claim in additionalClaims)
            {
                // Avoid duplicate claims
                if (principal.HasClaim(claim.Type, claim.Value))
                    continue;

                ((ClaimsIdentity?)principal.Identity)?.AddClaim(claim);
            }
        }

        return Task.FromResult(principal);
    }
}
