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
            WorkflowChatRunStartError.ProjectionUnavailable => StatusCodes.Status503ServiceUnavailable,
            WorkflowChatRunStartError.WorkflowBindingMismatch => StatusCodes.Status409Conflict,
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => StatusCodes.Status409Conflict,
            WorkflowChatRunStartError.InvalidWorkflowYaml => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.WorkflowNameMismatch => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.PromptRequired => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.InvalidCallerCredential => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.InvalidFileInput => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.InvalidChatHistory => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.InvalidConversationInput => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.InvalidConversationId => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.ConversationNotFound => StatusCodes.Status404NotFound,
            WorkflowChatRunStartError.ChatHistoryReservationUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
    }

    public static (string Code, string Message) ToCommandError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.AgentNotFound => ("AGENT_NOT_FOUND", "Agent not found."),
            WorkflowChatRunStartError.WorkflowNotFound => ("WORKFLOW_NOT_FOUND", WorkflowChatRunStartErrorGuidance.WorkflowNotFound),
            WorkflowChatRunStartError.AgentTypeNotSupported => ("AGENT_TYPE_NOT_SUPPORTED", "Actor is not workflow-capable."),
            WorkflowChatRunStartError.ProjectionDisabled => ("PROJECTION_DISABLED", "Projection pipeline is disabled."),
            WorkflowChatRunStartError.ProjectionUnavailable => ("WORKFLOW_PROJECTION_UNAVAILABLE", "Workflow projection is unavailable."),
            WorkflowChatRunStartError.WorkflowBindingMismatch => ("WORKFLOW_BINDING_MISMATCH", "Actor is bound to a different workflow."),
            WorkflowChatRunStartError.AgentWorkflowNotConfigured => ("AGENT_WORKFLOW_NOT_CONFIGURED", "Actor has no bound workflow."),
            WorkflowChatRunStartError.InvalidWorkflowYaml => ("INVALID_WORKFLOW_YAML", "Workflow YAML is invalid."),
            WorkflowChatRunStartError.WorkflowNameMismatch => ("WORKFLOW_NAME_MISMATCH", "Workflow name does not match workflow YAML."),
            WorkflowChatRunStartError.PromptRequired => ("PROMPT_REQUIRED", "Prompt is required."),
            WorkflowChatRunStartError.InvalidCallerCredential => ("INVALID_CALLER_CREDENTIAL", "Caller credential is invalid."),
            WorkflowChatRunStartError.InvalidFileInput => ("INVALID_FILE_INPUT", "File input is invalid."),
            WorkflowChatRunStartError.InvalidChatHistory => ("INVALID_CHAT_HISTORY", "Chat history intent is invalid."),
            WorkflowChatRunStartError.InvalidConversationInput => ("INVALID_CONVERSATION_INPUT", "Conversation input is invalid."),
            WorkflowChatRunStartError.InvalidConversationId => ("INVALID_CONVERSATION_ID", "Conversation id is invalid."),
            WorkflowChatRunStartError.ConversationNotFound => ("CONVERSATION_NOT_FOUND", "Conversation was not found."),
            WorkflowChatRunStartError.ChatHistoryReservationUnavailable => ("CHAT_HISTORY_RESERVATION_UNAVAILABLE", "Chat history reservation is unavailable."),
            _ => ("RUN_START_FAILED", "Failed to resolve actor."),
        };
    }
}
