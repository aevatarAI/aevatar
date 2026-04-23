using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace Aevatar.Authentication.Hosting;

public static class AevatarAuthenticationHostExtensions
{
    internal const string DisabledAuthenticationScheme = "AevatarDisabled";

    /// <summary>
    /// Registers JWT Bearer authentication by default.
    /// An explicit <c>Aevatar:Authentication:Enabled=false</c> only disables authentication
    /// when the host is running in Development.
    /// Provider-agnostic: uses OIDC discovery from the configured Authority.
    /// Requires an <see cref="IAevatarClaimsTransformer"/> to be registered by the provider package.
    /// </summary>
    public static WebApplicationBuilder AddAevatarAuthentication(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration
            .GetSection(AevatarAuthenticationOptions.SectionName)
            .Get<AevatarAuthenticationOptions>() ?? new AevatarAuthenticationOptions();
        var authenticationEnabled = ResolveAuthenticationEnabled(
            builder.Configuration[AevatarAuthenticationOptions.SectionName + ":Enabled"],
            builder.Environment);

        if (!authenticationEnabled)
        {
            builder.Services.AddAuthentication(DisabledAuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, DisabledAuthenticationHandler>(
                    DisabledAuthenticationScheme,
                    _ => { });
            builder.Services.AddAuthorization();
            return builder;
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

                jwt.TokenValidationParameters.ValidAudience = options.Audience;
                jwt.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience);
            });

        builder.Services.AddAuthorization();
        builder.Services.AddTransient<IClaimsTransformation, AevatarClaimsTransformation>();

        return builder;
    }

    internal static bool ResolveAuthenticationEnabled(string? configuredValue, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(configuredValue))
            return true;

        if (!bool.TryParse(configuredValue, out var enabled))
            throw new InvalidOperationException(
                $"Invalid boolean value '{configuredValue}' for {AevatarAuthenticationOptions.SectionName}:Enabled.");

        if (!enabled && !environment.IsDevelopment())
            return true;

        return enabled;
    }
}

internal sealed class DisabledAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DisabledAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        return Task.FromResult(AuthenticateResult.NoResult());
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
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
