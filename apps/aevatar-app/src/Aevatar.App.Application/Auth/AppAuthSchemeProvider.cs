using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aevatar.App.Application.Auth;

public static class AppAuthSchemeProvider
{
    public const string AppAuthScheme = "AppAuth";
    public const string FirebaseScheme = "Firebase";
    public const string TrialScheme = "Trial";

    public static string SelectScheme(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<AppAuthOptions>>().Value;
        var token = TryGetBearerToken(context);

        if (string.IsNullOrEmpty(token))
            return options.TrialAuthEnabled ? TrialScheme : FirebaseScheme;

        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            var iss = jwt.Issuer ?? string.Empty;
            var type = jwt.Claims.FirstOrDefault(c => c.Type == "type")?.Value;

            if (!string.IsNullOrEmpty(options.FirebaseProjectId)
                && iss.Contains($"securetoken.google.com/{options.FirebaseProjectId}", StringComparison.OrdinalIgnoreCase))
                return FirebaseScheme;

            if (options.TrialAuthEnabled && string.Equals(type, "trial", StringComparison.OrdinalIgnoreCase))
                return TrialScheme;
        }
        catch
        {
            // Fall through and pick the most likely configured scheme.
        }

        return options.TrialAuthEnabled ? TrialScheme : FirebaseScheme;
    }

    internal static string? TryGetBearerToken(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        return authHeader["Bearer ".Length..];
    }
}
