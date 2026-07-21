using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.AI.ToolProviders.StudioProvisioning;

internal static class StudioWorkflowCapabilityToolContext
{
    public static WorkflowCapabilityAdmissionContext Create(
        ExternalCapabilityExecutionMode executionMode)
    {
        var authority = AgentToolRequestContext.NyxIdAuthority;
        var callerId = authority.IsComplete
            ? authority.ExternalUserId
            : AgentToolRequestContext.OwnerSubject
              ?? AgentToolRequestContext.SenderNyxUserId;
        return new WorkflowCapabilityAdmissionContext(
            callerId ?? string.Empty,
            AgentToolRequestContext.NyxIdAccessToken,
            AgentToolRequestContext.NyxIdOrgToken,
            executionMode);
    }
}
