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
            WorkflowChatRunStartError.InvalidWorkflowYaml => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.WorkflowNameMismatch => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.PromptRequired => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.ConflictingScopeId => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.InvalidInputPart => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.InputPartTooLarge => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest,
        };
    }

    public static (string Code, string Message) ToCommandError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => ("AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => ("WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.AgentTypeNotSupported => ("AGENT_TYPE_NOT_SUPPORTED", "Actor is not workflow-capable."),
            WorkflowChatRunStartError.ProjectionDisabled => ("PROJECTION_DISABLED", "Projection pipeline is disabled."),
            WorkflowChatRunStartError.WorkflowBindingMismatch => ("WORKFLOW_BINDING_MISMATCH", "Actor is bound to a different workflow."),
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => ("AGENT_WORKFLOW_NOT_CONFIGURED", "Actor has no bound workflow."),
            WorkflowChatRunStartError.InvalidWorkflowYaml => ("INVALID_WORKFLOW_YAML", "Workflow YAML is invalid."),
            WorkflowChatRunStartError.WorkflowNameMismatch => ("WORKFLOW_NAME_MISMATCH", "Workflow name does not match workflow YAML."),
            WorkflowChatRunStartError.PromptRequired => ("PROMPT_REQUIRED", "Prompt is required."),
            WorkflowChatRunStartError.ConflictingScopeId => ("CONFLICTING_SCOPE_ID", "Conflicting scope_id values were provided."),
            WorkflowChatRunStartError.InvalidInputPart => ("INVALID_INPUT_PART", "One or more inputParts are invalid or unsupported."),
            WorkflowChatRunStartError.InputPartTooLarge => ("INPUT_PART_TOO_LARGE", "One or more inputParts exceed the allowed size."),
            _ => ("RUN_START_FAILED", "Failed to resolve actor."),
        };
    }
}
