using System.Net.Http.Headers;
using System.Security.Claims;
using Aevatar.Capabilities;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.AgentProfiles;

internal static class AgentProfileHttpCallerContext
{
    private const string IdentityProvider = "nyxid";
    private const string DevelopmentSubjectId = "development-user";
    private const string DevelopmentUsername = "development";
    private const string DevelopmentScopeId = "development";

    private static readonly string[] s_subjectClaimTypes =
    [
        "uid",
        ClaimTypes.NameIdentifier,
        "sub",
        "user_id",
    ];

    private static readonly string[] s_usernameClaimTypes =
    [
        "preferred_username",
        "username",
        "name",
    ];

    public static bool TryRequireScope(
        HttpContext http,
        string scopeId,
        out AgentProfileCallerContext caller,
        out IResult denied)
    {
        ArgumentNullException.ThrowIfNull(http);
        caller = null!;

        if (AevatarScopeAccessGuard.TryCreateScopeAccessDeniedResult(http, scopeId, out denied))
            return false;

        if (!AevatarScopeAccessGuard.IsAuthenticationEnabled(http.RequestServices))
        {
            caller = CreateDevelopment(scopeId);
            denied = Results.Empty;
            return true;
        }

        return TryCreateAuthenticated(http, scopeId, out caller, out denied);
    }

    public static bool TryRequireDiscovery(
        HttpContext http,
        out AgentProfileCallerContext caller,
        out IResult denied)
    {
        ArgumentNullException.ThrowIfNull(http);
        caller = null!;

        if (!AevatarScopeAccessGuard.IsAuthenticationEnabled(http.RequestServices))
        {
            caller = CreateDevelopment(DevelopmentScopeId);
            denied = Results.Empty;
            return true;
        }

        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
        {
            denied = AgentProfileHttpResults.Error(
                StatusCodes.Status401Unauthorized,
                "AGENT_PROFILE_AUTHENTICATION_REQUIRED");
            return false;
        }

        return TryCreateAuthenticated(http, scopeId, out caller, out denied);
    }

    private static bool TryCreateAuthenticated(
        HttpContext http,
        string scopeId,
        out AgentProfileCallerContext caller,
        out IResult denied)
    {
        caller = null!;
        var subjects = ReadDistinctClaims(http.User, s_subjectClaimTypes);
        if (subjects.Count != 1)
        {
            denied = AgentProfileHttpResults.Error(
                StatusCodes.Status401Unauthorized,
                "AGENT_PROFILE_SUBJECT_REQUIRED");
            return false;
        }

        var usernames = ReadDistinctClaims(http.User, s_usernameClaimTypes);
        caller = new AgentProfileCallerContext(
            new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = IdentityProvider,
                SubjectId = subjects[0],
            },
            scopeId,
            usernames.Count == 1 ? usernames[0] : null,
            ExtractBearer(http));
        denied = Results.Empty;
        return true;
    }

    private static AgentProfileCallerContext CreateDevelopment(string scopeId) =>
        new(
            new AgentProfileUserOwnerIdentity
            {
                IdentityProvider = IdentityProvider,
                SubjectId = DevelopmentSubjectId,
            },
            scopeId,
            DevelopmentUsername,
            null);

    private static IReadOnlyList<string> ReadDistinctClaims(
        ClaimsPrincipal? user,
        IReadOnlyCollection<string> claimTypes) =>
        user?.Claims
            .Where(claim => claimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .Select(static claim => claim.Value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

    private static string? ExtractBearer(HttpContext http)
    {
        var values = http.Request.Headers.Authorization;
        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header.Parameter?.Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
