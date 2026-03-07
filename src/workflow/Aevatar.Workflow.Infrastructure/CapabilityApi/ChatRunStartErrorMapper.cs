using Aevatar.Workflow.Application.Abstractions.Runs;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

internal static class ChatRunStartErrorMapper
{
    public static int ToHttpStatusCode(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.DefinitionActorNotFound => StatusCodes.Status404NotFound,
            WorkflowChatRunStartError.WorkflowNotFound => StatusCodes.Status404NotFound,
            WorkflowChatRunStartError.DefinitionActorTypeNotSupported => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.DefinitionBindingMismatch => StatusCodes.Status409Conflict,
            WorkflowChatRunStartError.DefinitionActorWorkflowNotConfigured => StatusCodes.Status409Conflict,
            WorkflowChatRunStartError.InvalidWorkflowYaml => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.WorkflowNameMismatch => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.DefinitionSourceConflict => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status400BadRequest,
        };
    }

    public static (string Code, string Message) ToCommandError(WorkflowChatRunStartError error)
    {
        return error switch
        {
            WorkflowChatRunStartError.DefinitionActorNotFound => ("DEFINITION_ACTOR_NOT_FOUND", "Definition actor not found."),
            WorkflowChatRunStartError.WorkflowNotFound => ("WORKFLOW_NOT_FOUND", "Workflow not found."),
            WorkflowChatRunStartError.DefinitionActorTypeNotSupported => ("DEFINITION_ACTOR_TYPE_NOT_SUPPORTED", "Definition actor is not workflow-bound."),
            WorkflowChatRunStartError.DefinitionBindingMismatch => ("DEFINITION_BINDING_MISMATCH", "Definition actor is bound to a different workflow."),
            WorkflowChatRunStartError.DefinitionActorWorkflowNotConfigured => ("DEFINITION_ACTOR_WORKFLOW_NOT_CONFIGURED", "Definition actor has no bound workflow."),
            WorkflowChatRunStartError.InvalidWorkflowYaml => ("INVALID_WORKFLOW_YAML", "Workflow YAML is invalid."),
            WorkflowChatRunStartError.WorkflowNameMismatch => ("WORKFLOW_NAME_MISMATCH", "Workflow name does not match workflow YAML."),
            WorkflowChatRunStartError.DefinitionSourceConflict => ("DEFINITION_SOURCE_CONFLICT", "Provide either definitionActorId or workflowYamls, not both."),
            _ => ("RUN_START_FAILED", "Failed to resolve workflow run."),
        };
    }
}
