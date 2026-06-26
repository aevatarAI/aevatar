using System.Security.Claims;
using Aevatar.Authentication.Abstractions;
using Aevatar.Authentication.ScopeServiceTokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
        var scopeTokenOptions = builder.Configuration
            .GetSection(ScopeServiceTokenOptions.SectionName)
            .Get<ScopeServiceTokenOptions>() ?? new ScopeServiceTokenOptions();
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

        if (scopeTokenOptions.Enabled)
            builder.Services.AddScopeServiceTokens(builder.Configuration);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

                jwt.TokenValidationParameters.ValidAudience = options.Audience;
                jwt.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience);
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if ((context.Request.Path.StartsWithSegments("/ws/voice") ||
                             context.Request.Path.StartsWithSegments("/whip/offer")) &&
                            context.Request.Query.TryGetValue("access_token", out var accessTokenValues))
                        {
                            var accessToken = accessTokenValues.FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(accessToken))
                                context.Token = accessToken.Trim();
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        if (scopeTokenOptions.Enabled)
        {
            builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IScopeServiceTokenKeyProvider>((jwt, keyProvider) =>
                    ConfigureScopeServiceTokenValidation(jwt, options, keyProvider));
        }

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

    private static void ConfigureScopeServiceTokenValidation(
        JwtBearerOptions jwt,
        AevatarAuthenticationOptions options,
        IScopeServiceTokenKeyProvider keyProvider)
    {
        var issuers = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Authority))
        {
            var authority = options.Authority.Trim();
            issuers.Add(authority);
            issuers.Add(authority.TrimEnd('/'));
        }
        issuers.Add(keyProvider.Issuer);

        jwt.TokenValidationParameters.ValidIssuers = issuers.Distinct(StringComparer.Ordinal).ToArray();
        var signingKeys = keyProvider.ValidationKeys.Select(key => key.ValidationKey).ToArray();
        jwt.TokenValidationParameters.IssuerSigningKeys = signingKeys;

        if (!string.IsNullOrWhiteSpace(keyProvider.Audience))
        {
            jwt.TokenValidationParameters.ValidAudiences = string.IsNullOrWhiteSpace(options.Audience)
                ? [keyProvider.Audience]
                : [options.Audience, keyProvider.Audience];
            jwt.TokenValidationParameters.ValidateAudience = true;
        }

        jwt.TokenValidationParameters.ClockSkew = keyProvider.ClockSkew;
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
