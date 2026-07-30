using System.Security.Claims;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Workflow.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.Endpoints;

internal static class WorkflowCapabilityAdmissionHttpContext
{
    private static readonly string[] s_callerIdClaimTypes =
    [
        "sub",
        ClaimTypes.NameIdentifier,
    ];

    public static WorkflowCapabilityAdmissionContext Create(
        HttpContext http,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        WorkflowCapabilityAdmissionPlan? existingPlan = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new WorkflowCapabilityAdmissionContext(
            ResolveCallerId(http.User),
            ExtractBearerToken(http),
            executionMode: executionMode,
            existingPlan: existingPlan);
    }

    private static string ResolveCallerId(ClaimsPrincipal? user)
    {
        foreach (var claimType in s_callerIdClaimTypes)
        {
            var values = user?.Claims
                .Where(claim => string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
                .Select(claim => claim.Value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            if (values.Length == 1)
                return values[0]!;
        }

        return string.Empty;
    }

    private static string? ExtractBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString().Trim();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var token = header["Bearer ".Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
