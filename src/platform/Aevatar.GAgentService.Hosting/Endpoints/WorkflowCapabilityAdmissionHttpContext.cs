using System.Security.Claims;
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

    public static WorkflowCapabilityAdmissionContext Create(
        HttpContext http,
        ExternalCapabilityExecutionMode executionMode = ExternalCapabilityExecutionMode.Interactive,
        WorkflowCapabilityAdmissionPlan? existingPlan = null,
        IEnumerable<NyxIdExplicitRequestConfirmationInput>? explicitRequestConfirmations = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        var extraction = WorkflowCallerCredentialExtractor.Extract(http);
        if (!extraction.Succeeded)
            throw new WorkflowCallerCredentialSelectionException();

        return new WorkflowCapabilityAdmissionContext(
            ResolveCallerId(http.User),
            extraction.NyxIdCredentialSelection,
            executionMode: executionMode,
            existingPlan: existingPlan,
            explicitRequestConfirmations: NyxIdExplicitRequestConfirmationInputs.ToConfirmations(
                explicitRequestConfirmations));
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

}
