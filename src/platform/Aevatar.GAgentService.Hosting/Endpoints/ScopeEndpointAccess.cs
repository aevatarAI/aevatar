using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.Endpoints;

internal static class ScopeEndpointAccess
{
    private static readonly string[] ScopeClaimTypes =
    [
        WorkflowRunCommandMetadataKeys.ScopeId,
        "scope_id",
    ];

    public static bool TryCreateScopeAccessDeniedResult(
        HttpContext http,
        string scopeId,
        out IResult denied)
    {
        if (!TryGetAuthenticatedScopeGuardFailure(http, scopeId, out var message))
        {
            denied = Results.Empty;
            return false;
        }

        denied = Results.Json(
            new
            {
                code = "SCOPE_ACCESS_DENIED",
                message,
            },
            statusCode: StatusCodes.Status403Forbidden);
        return true;
    }

    public static async Task<bool> TryWriteScopeAccessDeniedAsync(
        HttpContext http,
        string scopeId,
        CancellationToken ct)
    {
        if (!TryGetAuthenticatedScopeGuardFailure(http, scopeId, out var message))
            return false;

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsJsonAsync(
            new
            {
                code = "SCOPE_ACCESS_DENIED",
                message,
            },
            cancellationToken: ct);
        return true;
    }

    private static bool TryGetAuthenticatedScopeGuardFailure(
        HttpContext http,
        string requestedScopeId,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(http);

        message = string.Empty;
        if (http.User?.Identity?.IsAuthenticated != true)
        {
            message = "Authentication is required.";
            return true;
        }

        var normalizedRequestedScopeId = NormalizeRequired(requestedScopeId, nameof(requestedScopeId));
        var claimedScopeIds = http.User.Claims
            .Where(static claim => ScopeClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .Select(static claim => claim.Value?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (claimedScopeIds.Count == 0)
        {
            message = "Authenticated scope is missing.";
            return true;
        }

        if (claimedScopeIds.Count > 1)
        {
            message = "Authenticated scope is ambiguous.";
            return true;
        }

        if (string.Equals(claimedScopeIds[0], normalizedRequestedScopeId, StringComparison.Ordinal))
            return false;

        message = "Authenticated scope does not match requested scope.";
        return true;
    }

    private static string NormalizeRequired(string? value, string paramName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{paramName} is required.");

        return normalized;
    }
}
