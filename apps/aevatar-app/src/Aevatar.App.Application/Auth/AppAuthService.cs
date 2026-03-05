using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.App.Application.Auth;

public sealed record AuthUserInfo(
    string Id,
    string Email,
    string Provider,
    string ProviderId,
    bool EmailVerified);

public sealed class AppAuthOptions
{
    public string FirebaseProjectId { get; set; } = string.Empty;
    public string TrialTokenSecret { get; set; } = string.Empty;
    public bool TrialAuthEnabled { get; set; }
}

public sealed class AppAuthService : IAppAuthService
{
    private readonly AppAuthOptions _options;
    private readonly ILogger<AppAuthService> _logger;
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> _firebaseConfigManager;

    public AppAuthService(
        IOptions<AppAuthOptions> options,
        ILogger<AppAuthService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _firebaseConfigManager = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(() =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"https://securetoken.google.com/{_options.FirebaseProjectId}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever()));
    }

    public async Task<AuthUserInfo?> ValidateTokenAsync(string token)
    {
        var firebase = await ValidateFirebaseTokenAsync(token);
        if (firebase is not null) return firebase;

        if (_options.TrialAuthEnabled)
        {
            var trial = ValidateTrialToken(token);
            if (trial is not null) return trial;
        }

        _logger.LogDebug("Token validation failed for all providers");
        return null;
    }

    public async Task<AuthUserInfo?> ValidateFirebaseTokenAsync(string token)
    {
        if (string.IsNullOrEmpty(_options.FirebaseProjectId)) return null;

        try
        {
            var config = await _firebaseConfigManager.Value.GetConfigurationAsync(default);
            var parameters = new TokenValidationParameters
            {
                ValidIssuer = $"https://securetoken.google.com/{_options.FirebaseProjectId}",
                ValidAudience = _options.FirebaseProjectId,
                IssuerSigningKeys = config.SigningKeys,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(60)
            };

            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, parameters, out _);
            var uid = principal.FindFirst("user_id")?.Value ?? principal.FindFirst("sub")?.Value;
            var email = principal.FindFirst("email")?.Value;
            var verified = principal.FindFirst("email_verified")?.Value == "true";

            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(email))
                return null;

            _logger.LogDebug("Validated as Firebase token");
            return new AuthUserInfo(uid, email.ToLowerInvariant(), "firebase", uid, verified);
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogDebug("Firebase token expired");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Firebase token validation failed");
            return null;
        }
    }

    public AuthUserInfo? ValidateTrialToken(string token)
    {
        if (string.IsNullOrEmpty(_options.TrialTokenSecret)) return null;

        try
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.TrialTokenSecret));
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                IssuerSigningKey = key,
                ValidateLifetime = false,
                ClockSkew = TimeSpan.Zero
            };

            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, parameters, out _);
            var sub = principal.FindFirst("sub")?.Value;
            var email = principal.FindFirst("email")?.Value;

            if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email))
                return null;

            _logger.LogDebug("Validated as Trial token");
            return new AuthUserInfo(sub, email.ToLowerInvariant(), "trial", sub, false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Trial token validation failed");
            return null;
        }
    }
}
