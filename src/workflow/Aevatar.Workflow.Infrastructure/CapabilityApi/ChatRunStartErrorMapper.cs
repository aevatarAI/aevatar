using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatRunStartErrorMapper
{
    public static int ToHttpStatusCode(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => StatusCodes.Status404NotFound,
            WorkflowChatRunStartError.WorkflowNotFound => StatusCodes.Status404NotFound,
            WorkflowChatRunStartError.AgentTypeNotSupported => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.ProjectionDisabled => StatusCodes.Status503ServiceUnavailable,
            WorkflowChatRunStartError.WorkflowBindingMismatch => StatusCodes.Status409Conflict,
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
    }

    public static (string Code, string Message) ToCommandError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => ("AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => ("WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.AgentTypeNotSupported => ("AGENT_TYPE_NOT_SUPPORTED", "Agent is not WorkflowGAgent."),
            WorkflowChatRunStartError.ProjectionDisabled => ("PROJECTION_DISABLED", "Projection pipeline is disabled."),
            WorkflowChatRunStartError.WorkflowBindingMismatch => ("WORKFLOW_BINDING_MISMATCH", "Actor is bound to a different workflow."),
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => ("AGENT_WORKFLOW_NOT_CONFIGURED", "Actor has no bound workflow."),
            _ => ("RUN_START_FAILED", "Failed to resolve actor."),
        };
    }
}
