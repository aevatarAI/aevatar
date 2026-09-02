using System.Security.Claims;

namespace Aevatar.Capabilities;

public static class AevatarPrincipalSubjectResolver
{
    private static readonly string[] NyxIdSubjectClaimTypes =
    [
        "uid",
        "sub",
        ClaimTypes.NameIdentifier,
        "user_id",
    ];

    public static bool TryResolveNyxIdSubject(ClaimsPrincipal principal, out string subject)
    {
        ArgumentNullException.ThrowIfNull(principal);
        subject = string.Empty;
        if (principal.Identity?.IsAuthenticated != true)
            return false;

        var subjects = principal.Claims
            .Where(claim => NyxIdSubjectClaimTypes.Contains(
                claim.Type,
                StringComparer.OrdinalIgnoreCase))
            .Select(static claim => claim.Value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (subjects.Length != 1)
            return false;

        subject = subjects[0]!;
        return true;
    }
}
