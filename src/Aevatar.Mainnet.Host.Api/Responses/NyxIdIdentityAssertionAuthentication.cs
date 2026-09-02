using System.Security.Claims;
using System.Text.Encodings.Web;
using Aevatar.Authentication.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal static class NyxIdIdentityAssertionAuthentication
{
    internal const string Scheme = "NyxIdIdentityAssertion";
    internal const string HeaderName = "X-NyxID-Identity-Token";
    internal static readonly object ValidatedSubjectItemKey = new();

    internal static WebApplicationBuilder AddNyxIdIdentityAssertionAuthentication(
        this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, NyxIdIdentityAssertionAuthenticationHandler>(
                Scheme,
                _ => { });
        builder.Services.AddHttpContextAccessor();

        // Keep the existing bearer/DPoP path authoritative during the rollout. Once NyxID stops
        // forwarding the access token, Bearer transparently forwards authentication to the scoped
        // identity assertion scheme whenever the proxy-injected header is present.
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var previousSelector = options.ForwardDefaultSelector;
            options.ForwardDefaultSelector = context =>
            {
                var previousSelection = previousSelector?.Invoke(context);
                if (!string.IsNullOrWhiteSpace(previousSelection))
                    return previousSelection;

                if (!string.IsNullOrWhiteSpace(context.Request.Headers.Authorization.FirstOrDefault()))
                    return null;

                return context.Request.Headers.ContainsKey(HeaderName) ? Scheme : null;
            };
        });

        return builder;
    }
}

internal sealed class NyxIdIdentityAssertionAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly NyxIdIdentityAssertionValidator _validator;

    public NyxIdIdentityAssertionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        NyxIdIdentityAssertionValidator validator)
        : base(options, logger, encoder)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var values = Request.Headers[NyxIdIdentityAssertionAuthentication.HeaderName];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            return AuthenticateResult.Fail("NyxID identity assertion header must contain exactly one token.");

        var validation = await _validator.ValidateAsync(values[0]!, Context.RequestAborted);
        if (!validation.Succeeded || string.IsNullOrWhiteSpace(validation.Subject))
        {
            Logger.LogWarning(
                "NyxID identity assertion authentication failed: {ErrorCode}",
                validation.ErrorCode ?? "identity_assertion_invalid");
            return AuthenticateResult.Fail(
                $"NyxID identity assertion validation failed: {validation.ErrorCode ?? "identity_assertion_invalid"}.");
        }

        var subject = validation.Subject.Trim();
        var claims = (validation.Claims ?? [])
            .Where(static claim =>
                !string.Equals(claim.Type, AevatarStandardClaimTypes.ScopeId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(claim.Type, "workflow.scope_id", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The signed sub is the sole authority for tenant isolation. Even another signed claim
        // named scope_id cannot override it for this authentication scheme.
        claims.Add(new Claim(AevatarStandardClaimTypes.ScopeId, subject));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        Context.Items[NyxIdIdentityAssertionAuthentication.ValidatedSubjectItemKey] = subject;
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
