using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

public static class ChatRunStartErrorMapper
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
            WorkflowChatRunStartError.InvalidConversationInput => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.InvalidConversationId => StatusCodes.Status400BadRequest,
            WorkflowChatRunStartError.ConversationNotFound => StatusCodes.Status404NotFound,
            WorkflowChatRunStartError.ChatHistoryReservationUnavailable => StatusCodes.Status503ServiceUnavailable,
            WorkflowChatRunStartError.IdempotencyConflict => StatusCodes.Status409Conflict,
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
            WorkflowChatRunStartError.InvalidConversationInput => ("INVALID_CONVERSATION_INPUT", "Conversation input is invalid."),
            WorkflowChatRunStartError.InvalidConversationId => ("INVALID_CONVERSATION_ID", "Conversation id is invalid."),
            WorkflowChatRunStartError.ConversationNotFound => ("CONVERSATION_NOT_FOUND", "Conversation was not found."),
            WorkflowChatRunStartError.ChatHistoryReservationUnavailable => ("CHAT_HISTORY_RESERVATION_UNAVAILABLE", "Chat history reservation is unavailable."),
            WorkflowChatRunStartError.IdempotencyConflict => ("IDEMPOTENCY_CONFLICT", "Command id was already used for a different chat create request."),
            _ => ("RUN_START_FAILED", "Failed to resolve actor."),
        };
    }

    public static (string Code, string Message) ToCommandError(WorkflowChatRunStartFailureDetail? failureDetail)
    {
        if (failureDetail == null)
            return ToCommandError(WorkflowChatRunStartError.None);

        var mapped = ToCommandError(failureDetail.Error);
        return string.IsNullOrWhiteSpace(failureDetail.Message)
            ? mapped
            : (mapped.Code, failureDetail.Message);
    }

    public static object ToErrorBody(WorkflowChatRunStartError error) =>
        ToErrorBody(WorkflowChatRunStartFailureDetail.Create(error));

    public static object ToErrorBody(WorkflowChatRunStartFailureDetail? failureDetail)
    {
        var (code, message) = ToCommandError(failureDetail);
        var readiness = MapExternalCapabilityReadiness(failureDetail?.ExternalCapabilityReadiness);
        if (readiness == null)
        {
            return new
            {
                code,
                message,
            };
        }

        return new
        {
            code,
            message,
            externalCapabilityReadiness = readiness,
        };
    }

    private static object? MapExternalCapabilityReadiness(ExternalCapabilityReadiness? readiness)
    {
        if (readiness == null)
            return null;

        return new
        {
            status = ReadinessStatus(readiness.Status),
            selectedCapability = MapSelectedCapability(readiness),
            blockers = readiness.Blockers.Select(static blocker => new
            {
                status = ReadinessStatus(blocker.Status),
                code = blocker.Code,
                safeMessage = blocker.SafeMessage,
            }).ToArray(),
            remediations = readiness.Remediations.Select(static remediation => new
            {
                actionKind = RemediationKind(remediation.ActionKind),
                label = remediation.Label,
                trustedLocator = NullIfEmpty(remediation.TrustedLocator),
            }).ToArray(),
            sources = readiness.Sources.Select(static source => new
            {
                sourceKind = SourceKind(source.SourceKind),
                sourceId = source.SourceId,
                sourceVersion = source.SourceVersion,
            }).ToArray(),
        };
    }

    private static object? MapSelectedCapability(ExternalCapabilityReadiness readiness)
    {
        if (readiness.SelectedSelector is not null)
        {
            switch (readiness.SelectedSelector.SelectorCase)
            {
                case ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation:
                    return new
                    {
                        userServiceId = readiness.SelectedSelector.NyxIdOperation.UserServiceId,
                        endpointId = (string?)readiness.SelectedSelector.NyxIdOperation.EndpointId,
                        operationId = (string?)null,
                        connectorCapabilityRef = (string?)null,
                    };
                case ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest:
                    return new
                    {
                        userServiceId = readiness.SelectedSelector.NyxIdRequest.UserServiceId,
                        requestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
                            .ComputeNyxIdRequestContractDigest(readiness.SelectedSelector.NyxIdRequest),
                        endpointId = (string?)null,
                        operationId = (string?)null,
                        connectorCapabilityRef = (string?)null,
                    };
                case ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector:
                    return new
                    {
                        userServiceId = (string?)null,
                        endpointId = (string?)null,
                        operationId = (string?)readiness.SelectedSelector.HostConnector.OperationId,
                        connectorCapabilityRef = readiness.SelectedSelector.HostConnector.ConnectorCapabilityRef,
                    };
            }
        }

        return readiness.SelectedCapability?.CapabilityCase switch
        {
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService => new
            {
                userServiceId = (string?)readiness.SelectedCapability.NyxIdUserService.UserServiceId,
                endpointId = (string?)readiness.SelectedCapability.NyxIdUserService.EndpointId,
                operationId = (string?)null,
                connectorCapabilityRef = (string?)null,
            },
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest => new
            {
                userServiceId = (string?)readiness.SelectedCapability.NyxIdUserRequest.Request.UserServiceId,
                requestContractDigest = (string?)WorkflowCapabilityAdmissionPlanIntegrity
                    .ComputeNyxIdRequestContractDigest(readiness.SelectedCapability.NyxIdUserRequest.Request),
                endpointId = (string?)null,
                operationId = (string?)null,
                connectorCapabilityRef = (string?)null,
            },
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector => new
            {
                userServiceId = (string?)null,
                endpointId = (string?)null,
                operationId = (string?)readiness.SelectedCapability.HostConnector.OperationId,
                connectorCapabilityRef = (string?)readiness.SelectedCapability.HostConnector.ConnectorCapabilityRef,
            },
            _ => null,
        };
    }

    private static string ReadinessStatus(ExternalCapabilityReadinessStatus status) => status switch
    {
        ExternalCapabilityReadinessStatus.SelectionRequired => "selection_required",
        ExternalCapabilityReadinessStatus.ConnectorNotFound => "connector_not_found",
        ExternalCapabilityReadinessStatus.ServiceRegistrationRequired => "service_registration_required",
        ExternalCapabilityReadinessStatus.CredentialConnectionRequired => "credential_connection_required",
        ExternalCapabilityReadinessStatus.ServiceAccessDenied => "service_access_denied",
        ExternalCapabilityReadinessStatus.NodeBindingRequired => "node_binding_required",
        ExternalCapabilityReadinessStatus.NodeUnavailable => "node_unavailable",
        ExternalCapabilityReadinessStatus.EndpointContractRequired => "endpoint_contract_required",
        ExternalCapabilityReadinessStatus.OperationSelectionRequired => "operation_selection_required",
        ExternalCapabilityReadinessStatus.SourceStale => "source_stale",
        ExternalCapabilityReadinessStatus.DurableAuthorizationUnavailable => "durable_authorization_unavailable",
        ExternalCapabilityReadinessStatus.ContractDrift => "contract_drift",
        ExternalCapabilityReadinessStatus.Ready => "ready",
        ExternalCapabilityReadinessStatus.AdmissionRebindRequired => "admission_rebind_required",
        _ => "unspecified",
    };

    private static string RemediationKind(ExternalCapabilityRemediationActionKind actionKind) =>
        actionKind switch
        {
            ExternalCapabilityRemediationActionKind.SelectCapability => "select_capability",
            ExternalCapabilityRemediationActionKind.ConfigureConnector => "configure_connector",
            ExternalCapabilityRemediationActionKind.RegisterService => "register_service",
            ExternalCapabilityRemediationActionKind.ConnectCredential => "connect_credential",
            ExternalCapabilityRemediationActionKind.RequestAccess => "request_access",
            ExternalCapabilityRemediationActionKind.BindNode => "bind_node",
            ExternalCapabilityRemediationActionKind.RestoreNode => "restore_node",
            ExternalCapabilityRemediationActionKind.PublishEndpointContract => "publish_endpoint_contract",
            ExternalCapabilityRemediationActionKind.SelectOperation => "select_operation",
            ExternalCapabilityRemediationActionKind.RefreshSource => "refresh_source",
            ExternalCapabilityRemediationActionKind.UseInteractiveExecution => "use_interactive_execution",
            ExternalCapabilityRemediationActionKind.RebindWorkflow => "rebind_workflow",
            _ => "unspecified",
        };

    private static string SourceKind(ExternalCapabilitySourceKind sourceKind) => sourceKind switch
    {
        ExternalCapabilitySourceKind.ConnectorCatalog => "connector_catalog",
        ExternalCapabilitySourceKind.NyxIdUserServices => "nyx_id_user_services",
        ExternalCapabilitySourceKind.NyxIdOpenApi => "nyx_id_open_api",
        ExternalCapabilitySourceKind.NyxIdMcpConfig => "nyx_id_mcp_config",
        ExternalCapabilitySourceKind.DurableAuthorizationCatalog => "durable_authorization_catalog",
        _ => "unspecified",
    };

    private static string? NullIfEmpty(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
