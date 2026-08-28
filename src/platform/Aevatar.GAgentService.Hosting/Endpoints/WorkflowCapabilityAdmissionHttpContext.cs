using System.Security.Claims;
using Aevatar.Capabilities;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Http;

namespace Aevatar.GAgentService.Hosting.Endpoints;

internal static class WorkflowCapabilityAdmissionHttpContext
{
    private static readonly string[] s_callerIdClaimTypes =
    [
        "sub",
        ClaimTypes.NameIdentifier,
    ];

    public static async ValueTask<WorkflowCapabilityAdmissionContext> CreateAsync(
        HttpContext http,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        WorkflowCapabilityAdmissionPlan? existingPlan = null,
        IEnumerable<NyxIdExplicitRequestConfirmationInput>? explicitRequestConfirmations = null,
        CancellationToken ct = default,
        string? authenticationDisabledCallerId = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        var extraction = await WorkflowCallerCredentialExtractor.ExtractAsync(http, ct);
        if (!extraction.Succeeded)
            throw new WorkflowCallerCredentialSelectionException();

        return new WorkflowCapabilityAdmissionContext(
            ResolveCallerId(http, authenticationDisabledCallerId),
            extraction.NyxIdCredentialSelection,
            executionMode: executionMode,
            existingPlan: existingPlan,
            explicitRequestConfirmations: NyxIdExplicitRequestConfirmationInputs.ToConfirmations(
                explicitRequestConfirmations));
    }

    private static string ResolveCallerId(
        HttpContext http,
        string? authenticationDisabledCallerId)
    {
        foreach (var claimType in s_callerIdClaimTypes)
        {
            var values = http.User?.Claims
                .Where(claim => string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase))
                .Select(claim => claim.Value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            if (values.Length == 1)
                return values[0]!;
        }

        var normalizedFallback = authenticationDisabledCallerId?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedFallback) &&
            !AevatarScopeAccessGuard.IsAuthenticationEnabled(http.RequestServices))
        {
            return normalizedFallback;
        }

        return string.Empty;
    }

}
