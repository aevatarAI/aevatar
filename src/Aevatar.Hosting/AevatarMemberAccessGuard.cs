using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Hosting;

public static class AevatarMemberAccessGuard
{
    private const string MemberIdClaimType = "member_id";
    private const string UserIdClaimType = "user_id";
    private const string ScopeRoleClaimType = "scope_role";
    private const string DottedScopeRoleClaimType = "scope.role";
    private const string PlainRoleClaimType = "role";

    private static readonly string[] MemberClaimTypes =
    [
        MemberIdClaimType,
        UserIdClaimType,
        "uid",
        "sub",
        ClaimTypes.NameIdentifier,
    ];

    private static readonly string[] RoleClaimTypes =
    [
        ScopeRoleClaimType,
        DottedScopeRoleClaimType,
        PlainRoleClaimType,
        ClaimTypes.Role,
    ];

    private static readonly HashSet<string> ScopeAdminRoleValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "owner",
        "scope-admin",
        "scope_admin",
    };

    public static bool TryCreateMemberAccessDeniedResult(
        HttpContext http,
        string memberId,
        out IResult denied)
    {
        if (!TryGetAuthenticatedMemberGuardFailure(http, memberId, requireScopeAdmin: false, out var code, out var message))
        {
            denied = Results.Empty;
            return false;
        }

        denied = Results.Json(
            new
            {
                code,
                message,
            },
            statusCode: StatusCodes.Status403Forbidden);
        return true;
    }

    public static bool TryCreateScopeAdminRequiredResult(
        HttpContext http,
        string memberId,
        out IResult denied)
    {
        if (!TryGetAuthenticatedMemberGuardFailure(http, memberId, requireScopeAdmin: true, out var code, out var message))
        {
            denied = Results.Empty;
            return false;
        }

        denied = Results.Json(
            new
            {
                code,
                message,
            },
            statusCode: StatusCodes.Status403Forbidden);
        return true;
    }

    public static async Task<bool> TryWriteMemberAccessDeniedAsync(
        HttpContext http,
        string memberId,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedMemberGuardFailure(http, memberId, requireScopeAdmin: false, out var code, out var message))
            return false;

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(
            new
            {
                code,
                message,
            },
            cancellationToken: ct);
        return true;
    }

    private static bool TryGetAuthenticatedMemberGuardFailure(
        HttpContext http,
        string requestedMemberId,
        bool requireScopeAdmin,
        out string code,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(http);

        code = string.Empty;
        message = string.Empty;
        if (!AevatarScopeAccessGuard.IsAuthenticationEnabled(http.RequestServices))
            return false;

        if (http.User?.Identity?.IsAuthenticated != true)
        {
            code = "MEMBER_ACCESS_DENIED";
            message = "Authentication is required.";
            return true;
        }

        var normalizedRequestedMemberId = NormalizeRequired(requestedMemberId, nameof(requestedMemberId));
        if (HasScopeAdminRole(http.User))
            return false;

        if (requireScopeAdmin)
        {
            code = "SCOPE_ADMIN_REQUIRED";
            message = "Scope administrator access is required for member binding until the member catalog is authoritative.";
            return true;
        }

        if (HasMatchingMemberClaim(http.User, normalizedRequestedMemberId))
            return false;

        code = "MEMBER_ACCESS_DENIED";
        message = "Authenticated member does not match requested member.";
        return true;
    }

    private static bool HasScopeAdminRole(ClaimsPrincipal user) =>
        user.Claims.Any(static claim =>
            RoleClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase)
            && ScopeAdminRoleValues.Contains(claim.Value?.Trim() ?? string.Empty));

    private static bool HasMatchingMemberClaim(ClaimsPrincipal user, string requestedMemberId) =>
        user.Claims.Any(claim =>
            MemberClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase)
            && string.Equals(claim.Value?.Trim(), requestedMemberId, StringComparison.Ordinal));

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{paramName} is required.");

        return normalized;
    }
}
