using Aevatar.Workflow.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.Studio.Hosting.Endpoints;

internal static class StudioExternalCapabilityAdmissionHttpMapper
{
    private const string ErrorCode = "STUDIO_MEMBER_EXTERNAL_CAPABILITY_NOT_READY";
    private const string SafeMessage = "External workflow capability admission failed.";

    public static IResult BadRequest(ExternalCapabilityReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        return Results.BadRequest(new StudioExternalCapabilityAdmissionErrorResponse(
            ErrorCode,
            SafeMessage,
            MapReadiness(readiness)));
    }

    private static StudioExternalCapabilityReadinessResponse MapReadiness(
        ExternalCapabilityReadiness readiness) =>
        new(
            ReadinessStatus(readiness.Status),
            MapSelectedCapability(readiness),
            readiness.Blockers.Select(static blocker => new StudioExternalCapabilityBlockerResponse(
                    ReadinessStatus(blocker.Status),
                    blocker.Code,
                    blocker.SafeMessage))
                .ToArray(),
            readiness.Remediations.Select(static remediation =>
                    new StudioExternalCapabilityRemediationResponse(
                        RemediationKind(remediation.ActionKind),
                        remediation.Label,
                        NullIfEmpty(remediation.TrustedLocator)))
                .ToArray(),
            readiness.Sources.Select(static source => new StudioExternalCapabilitySourceResponse(
                    SourceKind(source.SourceKind),
                    source.SourceId,
                    source.SourceVersion))
                .ToArray());

    private static StudioExternalCapabilitySelectionResponse? MapSelectedCapability(
        ExternalCapabilityReadiness readiness)
    {
        if (readiness.SelectedSelector is not null)
        {
            switch (readiness.SelectedSelector.SelectorCase)
            {
                case ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation:
                    return new StudioExternalCapabilitySelectionResponse(
                        readiness.SelectedSelector.NyxIdOperation.UserServiceId,
                        readiness.SelectedSelector.NyxIdOperation.EndpointId,
                        null,
                        null);
                case ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector:
                    return new StudioExternalCapabilitySelectionResponse(
                        null,
                        null,
                        readiness.SelectedSelector.HostConnector.OperationId,
                        readiness.SelectedSelector.HostConnector.ConnectorCapabilityRef);
            }
        }

        return readiness.SelectedCapability?.CapabilityCase switch
        {
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService =>
                new StudioExternalCapabilitySelectionResponse(
                    readiness.SelectedCapability.NyxIdUserService.UserServiceId,
                    readiness.SelectedCapability.NyxIdUserService.EndpointId,
                    null,
                    null),
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.HostConnector =>
                new StudioExternalCapabilitySelectionResponse(
                    null,
                    null,
                    readiness.SelectedCapability.HostConnector.OperationId,
                    readiness.SelectedCapability.HostConnector.ConnectorCapabilityRef),
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed record StudioExternalCapabilityAdmissionErrorResponse(
    string Code,
    string Message,
    StudioExternalCapabilityReadinessResponse Readiness);

internal sealed record StudioExternalCapabilityReadinessResponse(
    string Status,
    StudioExternalCapabilitySelectionResponse? SelectedCapability,
    IReadOnlyList<StudioExternalCapabilityBlockerResponse> Blockers,
    IReadOnlyList<StudioExternalCapabilityRemediationResponse> Remediations,
    IReadOnlyList<StudioExternalCapabilitySourceResponse> Sources);

internal sealed record StudioExternalCapabilitySelectionResponse(
    string? UserServiceId,
    string? EndpointId,
    string? OperationId,
    string? ConnectorCapabilityRef);

internal sealed record StudioExternalCapabilityBlockerResponse(
    string Status,
    string Code,
    string SafeMessage);

internal sealed record StudioExternalCapabilityRemediationResponse(
    string ActionKind,
    string Label,
    string? TrustedLocator);

internal sealed record StudioExternalCapabilitySourceResponse(
    string SourceKind,
    string SourceId,
    long SourceVersion);
