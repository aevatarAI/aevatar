using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal static class StudioWorkflowCapabilityToolContext
{
    public static WorkflowCapabilityAdmissionContext? Resolve(
        ExternalCapabilityExecutionMode executionMode)
    {
        var authority = AgentToolRequestContext.NyxIdAuthority;
        if (!authority.IsComplete)
            return null;

        return new WorkflowCapabilityAdmissionContext(
            authority.ExternalUserId!,
            NyxIdCallerCredentialSelection.SourceReadableUserBearerOrNull(
                AgentToolSourceReadableNyxIdCredential.ResolveBearerToken(
                    AgentToolRequestContext.Current?.Credentials)),
            AgentToolRequestContext.NyxIdOrgToken,
            executionMode);
    }
}
