using System.Security.Claims;

namespace Aevatar.App.Application.Auth;

public static class AuthPrincipalExtensions
{
    public static AuthUserInfo? ToAuthUserInfo(this ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var id = principal.FindFirst("sub")?.Value;
        var email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;
        var provider = principal.FindFirst("provider")?.Value;
        var providerId = principal.FindFirst("provider_id")?.Value ?? id;
        var emailVerified = principal.FindFirst("email_verified")?.Value == "true";

        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(provider)
            || string.IsNullOrWhiteSpace(providerId))
            return null;

        return new AuthUserInfo(
            id,
            email.ToLowerInvariant(),
            provider,
            providerId,
            emailVerified);
    }
}
