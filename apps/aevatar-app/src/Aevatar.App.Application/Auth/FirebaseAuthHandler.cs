using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Auth;

public sealed class FirebaseAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAppAuthService _authService;

    public FirebaseAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAppAuthService authService)
        : base(options, logger, encoder)
    {
        _authService = authService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = AppAuthSchemeProvider.TryGetBearerToken(Context);
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.NoResult();

        var authUser = await _authService.ValidateFirebaseTokenAsync(token);
        if (authUser is null)
            return AuthenticateResult.Fail("Invalid Firebase token");

        var claims = new[]
        {
            new Claim("sub", authUser.Id),
            new Claim(ClaimTypes.Email, authUser.Email),
            new Claim("email", authUser.Email),
            new Claim("provider", authUser.Provider),
            new Claim("provider_id", authUser.ProviderId),
            new Claim("email_verified", authUser.EmailVerified ? "true" : "false"),
        };

        var identity = new ClaimsIdentity(claims, AppAuthSchemeProvider.FirebaseScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
