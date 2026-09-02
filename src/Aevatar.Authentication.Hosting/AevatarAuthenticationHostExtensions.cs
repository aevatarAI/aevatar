using System.Security.Claims;
using System.Net.Http.Headers;
using System.Text.Json;
using Aevatar.Authentication.Abstractions;
using Aevatar.Authentication.ScopeServiceTokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text.Encodings.Web;

namespace Aevatar.Authentication.Hosting;

public static class AevatarAuthenticationHostExtensions
{
    internal const string DisabledAuthenticationScheme = "AevatarDisabled";
    private static readonly object RawAccessTokenItemKey = new();

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

        var apiAudience = ResolveApiAudience(options.Audience, builder.Environment);
        if (scopeTokenOptions.Enabled && string.IsNullOrWhiteSpace(scopeTokenOptions.Audience))
        {
            throw new InvalidOperationException(
                "Aevatar:Authentication:ScopeServiceTokens:Audience is required when scope service tokens are enabled.");
        }

        if (scopeTokenOptions.Enabled)
            builder.Services.AddScopeServiceTokens(builder.Configuration);

        // Bind options so the DPoP OnTokenValidated hook can read DPoP settings at request time.
        builder.Services.AddOptions<AevatarAuthenticationOptions>()
            .Bind(builder.Configuration.GetSection(AevatarAuthenticationOptions.SectionName));

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;

                jwt.TokenValidationParameters.ValidAudience = apiAudience;
                jwt.TokenValidationParameters.ValidateAudience = apiAudience != null;

                // Pin the accepted signing algorithms to block alg=none and downgrade attacks.
                // Default is a safe superset covering every algorithm actually accepted; when
                // scope service tokens are enabled it also allows their HS256/RS256 keys.
                jwt.TokenValidationParameters.ValidAlgorithms =
                    ResolveValidAlgorithms(options, scopeTokenOptions.Enabled);

                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = OnTokenValidatedValidateDPoP,
                    OnMessageReceived = context =>
                    {
                        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
                        if (AuthenticationHeaderValue.TryParse(authorization, out var header) &&
                            !string.IsNullOrWhiteSpace(header.Parameter))
                        {
                            if (string.Equals(header.Scheme, "DPoP", StringComparison.OrdinalIgnoreCase))
                                context.Token = header.Parameter.Trim();

                            if (string.Equals(header.Scheme, "DPoP", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                            {
                                context.HttpContext.Items[RawAccessTokenItemKey] = header.Parameter.Trim();
                            }
                        }

                        if (context.Request.Path.StartsWithSegments("/ws/voice") ||
                            context.Request.Path.StartsWithSegments("/whip/offer"))
                        {
                            // Preferred: token carried in the Sec-WebSocket-Protocol header, so
                            // it never appears in the request URL (request-URL logging → stdout
                            // → Elasticsearch/ingress logs). See WebSocketSubprotocolToken.
                            var subprotocolToken = WebSocketSubprotocolToken.ExtractBearer(
                                context.HttpContext.WebSockets.WebSocketRequestedProtocols);
                            if (!string.IsNullOrWhiteSpace(subprotocolToken))
                            {
                                context.Token = subprotocolToken;
                                context.HttpContext.Items[RawAccessTokenItemKey] = subprotocolToken;
                            }
                            // Fallback: legacy ?access_token= query param (older clients). The
                            // request-log redactor scrubs it from logs; new clients should use
                            // the subprotocol so the token never reaches a URL at all.
                            else if (context.Request.Query.TryGetValue("access_token", out var accessTokenValues))
                            {
                                var accessToken = accessTokenValues.FirstOrDefault();
                                if (!string.IsNullOrWhiteSpace(accessToken))
                                {
                                    context.Token = accessToken.Trim();
                                    context.HttpContext.Items[RawAccessTokenItemKey] = context.Token;
                                }
                            }
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        if (scopeTokenOptions.Enabled)
        {
            builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IScopeServiceTokenKeyProvider>((jwt, keyProvider) =>
                    ConfigureScopeServiceTokenValidation(jwt, options, apiAudience, keyProvider));
        }

        // DPoP (RFC 9449) sender-constraint validation. Hosts enabling it must replace the
        // no-op replay guard with a shared atomic implementation; the startup validator below
        // fails closed if that production requirement is missed.
        builder.Services.TryAddSingleton<IDPoPReplayGuard, NoOpDPoPReplayGuard>();
        builder.Services.TryAddSingleton<DPoPProofValidator>();
        builder.Services.AddHostedService<DPoPReplayGuardStartupValidator>();

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
        string? apiAudience,
        IScopeServiceTokenKeyProvider keyProvider)
    {
        var issuers = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Authority))
        {
            var authority = options.Authority.Trim();
            issuers.Add(authority);
            issuers.Add(authority.TrimEnd('/'));
        }
        if (issuers.Contains(keyProvider.Issuer, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Scope service token issuer must be distinct from the OIDC authority issuer.");
        }

        var authorityIssuers = issuers.Distinct(StringComparer.Ordinal).ToArray();
        issuers.Add(keyProvider.Issuer);

        jwt.TokenValidationParameters.ValidIssuers = issuers.Distinct(StringComparer.Ordinal).ToArray();
        var signingKeys = keyProvider.ValidationKeys.Select(key => key.ValidationKey).ToArray();
        jwt.TokenValidationParameters.IssuerSigningKeys = signingKeys;

        // Bind scope tokens to local scope keys and authority tokens to discovery keys. Unknown
        // issuers receive no keys, so signature resolution itself preserves the issuer boundary.
        var resolver = new PerIssuerSigningKeyResolver(
            authorityIssuers,
            [keyProvider.Issuer],
            signingKeys);
        jwt.TokenValidationParameters.IssuerSigningKeyResolver = null;
        jwt.TokenValidationParameters.IssuerSigningKeyResolverUsingConfiguration = resolver.Resolve;
        jwt.TokenValidationParameters.IssuerValidator = null;
        jwt.TokenValidationParameters.IssuerValidatorUsingConfiguration = resolver.ValidateIssuer;
        jwt.TokenValidationParameters.ValidateIssuer = true;

        jwt.TokenValidationParameters.ValidAudience = null;
        jwt.TokenValidationParameters.ValidAudiences = null;
        jwt.TokenValidationParameters.AudienceValidator = (audiences, securityToken, _) =>
            ValidateAudienceForIssuer(
                audiences,
                securityToken,
                apiAudience,
                keyProvider.Issuer,
                keyProvider.Audience);
        jwt.TokenValidationParameters.ValidateAudience = true;

        jwt.TokenValidationParameters.ClockSkew = keyProvider.ClockSkew;
    }

    private static bool ValidateAudienceForIssuer(
        IEnumerable<string> audiences,
        SecurityToken securityToken,
        string? apiAudience,
        string scopeIssuer,
        string? scopeAudience)
    {
        var isScopeIssuer = string.Equals(
            securityToken?.Issuer,
            scopeIssuer,
            StringComparison.Ordinal);
        if (isScopeIssuer)
        {
            return scopeAudience != null &&
                   audiences.Contains(scopeAudience, StringComparer.Ordinal);
        }

        return apiAudience == null ||
               audiences.Contains(apiAudience, StringComparer.Ordinal);
    }

    // Asymmetric OIDC algorithms every legitimate NyxID/OIDC token may be signed with. Kept a
    // superset so a valid token is never rejected for a legitimately-used algorithm; alg=none
    // and symmetric downgrade are excluded by omission.
    private static readonly string[] AsymmetricOidcAlgorithms =
    [
        "RS256", "RS384", "RS512", "ES256", "ES384", "PS256",
    ];

    internal static string[] ResolveValidAlgorithms(
        AevatarAuthenticationOptions options,
        bool scopeServiceTokensEnabled)
    {
        // Explicit override wins (deployment knows its exact set).
        if (options.ValidAlgorithms is { Length: > 0 })
            return options.ValidAlgorithms;

        var algorithms = new HashSet<string>(AsymmetricOidcAlgorithms, StringComparer.Ordinal);

        // Scope service tokens are signed with HS256 or RS256 (see ScopeServiceTokenAlgorithms).
        if (scopeServiceTokensEnabled)
        {
            algorithms.Add("HS256");
            algorithms.Add("RS256");
        }

        return algorithms.ToArray();
    }

    // No-op unless DPoP is enabled: default bearer behavior is unchanged. When enabled and the
    // validated access token carries a cnf.jkt confirmation, a matching DPoP proof header is
    // required (RFC 9449). Access tokens without cnf.jkt are left untouched (plain bearer).
    private static async Task OnTokenValidatedValidateDPoP(TokenValidatedContext context)
    {
        var options = context.HttpContext.RequestServices
            .GetService<IOptions<AevatarAuthenticationOptions>>()?.Value;
        if (options is null || !options.DPoP.Enabled)
            return;

        var confirmationThumbprint = ExtractConfirmationThumbprint(context.Principal);
        if (confirmationThumbprint is null)
            return; // Not a sender-constrained token; nothing to enforce.

        var proofToken = context.Request.Headers["DPoP"].FirstOrDefault();
        var accessToken = context.HttpContext.Items.TryGetValue(RawAccessTokenItemKey, out var rawToken)
            ? rawToken as string
            : null;
        var validator = context.HttpContext.RequestServices.GetRequiredService<DPoPProofValidator>();
        var request = context.Request;
        var requestUri = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";

        var result = await validator.ValidateAsync(
            proofToken,
            accessToken,
            confirmationThumbprint,
            request.Method,
            requestUri,
            options.DPoP.IatSkewSeconds,
            context.HttpContext.RequestAborted);

        if (!result.Succeeded)
            context.Fail($"DPoP validation failed: {result.ErrorCode}");
    }

    private static string? ExtractConfirmationThumbprint(ClaimsPrincipal? principal)
    {
        // RFC 9449/7800: the access token carries a "cnf" claim whose "jkt" member is the
        // JWK thumbprint of the sender's proof key. The JWT handler surfaces the cnf object as
        // a JSON string claim; parse it and read jkt.
        var cnf = principal?.FindFirst("cnf")?.Value;
        if (string.IsNullOrWhiteSpace(cnf))
            return null;

        try
        {
            using var document = JsonDocument.Parse(cnf);
            if (document.RootElement.TryGetProperty("jkt", out var jkt) &&
                jkt.ValueKind == JsonValueKind.String)
            {
                var value = jkt.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
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

    private static string? ResolveApiAudience(string? configuredAudience, IHostEnvironment environment)
    {
        var audience = string.IsNullOrWhiteSpace(configuredAudience)
            ? null
            : configuredAudience.Trim();
        if (audience == null && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{AevatarAuthenticationOptions.SectionName}:Audience is required when " +
                "authentication is enabled outside Development.");
        }

        return audience;
    }
}

internal sealed class DPoPReplayGuardStartupValidator : IHostedService
{
    private readonly IOptions<AevatarAuthenticationOptions> _options;
    private readonly IDPoPReplayGuard _replayGuard;

    public DPoPReplayGuardStartupValidator(
        IOptions<AevatarAuthenticationOptions> options,
        IDPoPReplayGuard replayGuard)
    {
        _options = options;
        _replayGuard = replayGuard;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Value.DPoP.Enabled && _replayGuard is NoOpDPoPReplayGuard)
        {
            throw new InvalidOperationException(
                "DPoP is enabled but no shared IDPoPReplayGuard is registered. " +
                "Configure an atomic TTL-backed replay guard before starting the host.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
