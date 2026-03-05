using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Auth;

public sealed class TrialAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAppAuthService _authService;
    private readonly IOptions<AppAuthOptions> _appOptions;

    public TrialAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAppAuthService authService,
        IOptions<AppAuthOptions> appOptions)
        : base(options, logger, encoder)
    {
        _authService = authService;
        _appOptions = appOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_appOptions.Value.TrialAuthEnabled)
            return Task.FromResult(AuthenticateResult.Fail("Trial auth disabled"));

        var token = AppAuthSchemeProvider.TryGetBearerToken(Context);
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var authUser = _authService.ValidateTrialToken(token);
        if (authUser is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid trial token"));

        var claims = new[]
        {
            new Claim("sub", authUser.Id),
            new Claim(ClaimTypes.Email, authUser.Email),
            new Claim("email", authUser.Email),
            new Claim("provider", authUser.Provider),
            new Claim("provider_id", authUser.ProviderId),
            new Claim("email_verified", authUser.EmailVerified ? "true" : "false"),
            new Claim("type", "trial"),
        };

        var identity = new ClaimsIdentity(claims, AppAuthSchemeProvider.TrialScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
