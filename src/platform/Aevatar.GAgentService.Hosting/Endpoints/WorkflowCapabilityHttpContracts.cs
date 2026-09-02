using Aevatar.Workflow.Abstractions;

namespace Aevatar.GAgentService.Hosting.Endpoints;

public sealed record WorkflowCapabilitySelectorHttpRequest(
    string Kind,
    string UserServiceId,
    string EndpointId);

public sealed record WorkflowCapabilityReadinessHttpRequest(
    WorkflowCapabilitySelectorHttpRequest Selector,
    string ExecutionMode);

public sealed record WorkflowCapabilityListHttpResponse(
    IReadOnlyList<WorkflowCapabilityDescriptorHttpResponse> Capabilities,
    int CandidateCount,
    int RejectedCount,
    IReadOnlyList<WorkflowCapabilityDiagnosticHttpResponse> Diagnostics);

public sealed record WorkflowCapabilityDescriptorHttpResponse(
    string DisplayName,
    bool ReadOnly,
    bool Destructive,
    WorkflowCapabilitySelectorHttpResponse Selector,
    WorkflowCapabilitySourceHttpResponse? Source);

public sealed record WorkflowCapabilitySelectorHttpResponse(
    string Kind,
    string? UserServiceId = null,
    string? EndpointId = null,
    string? ConnectorCapabilityRef = null,
    string? OperationId = null,
    string? ContractDigest = null,
    string? Method = null,
    string? PathTemplate = null,
    IReadOnlyList<string>? QueryParameters = null,
    IReadOnlyList<string>? HeaderParameters = null,
    string? BodyMode = null,
    string? ResponseMode = null,
    bool? BodyRequired = null);

public sealed record WorkflowCapabilitySourceHttpResponse(
    string Kind,
    string SourceId,
    long SourceVersion,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? FreshUntil);

public sealed record WorkflowCapabilityDiagnosticHttpResponse(
    string Code,
    string SafeMessage,
    int Count,
    WorkflowCapabilitySourceHttpResponse? Source);

public sealed record WorkflowCapabilityReadinessHttpResponse(
    string ExecutionMode,
    string Status,
    WorkflowCapabilitySelectorHttpResponse? SelectedSelector,
    WorkflowCapabilityOperationHttpResponse? SelectedOperation,
    IReadOnlyList<WorkflowCapabilityBlockerHttpResponse> Blockers,
    IReadOnlyList<WorkflowCapabilityRemediationHttpResponse> Remediations,
    IReadOnlyList<WorkflowCapabilitySourceHttpResponse> Sources);

public sealed record WorkflowCapabilityBlockerHttpResponse(
    string Status,
    string Code,
    string SafeMessage);

public sealed record WorkflowCapabilityRemediationHttpResponse(
    string ActionKind,
    string Label,
    string TrustedLocator);

public sealed record WorkflowCapabilityOperationHttpResponse(
    string UserServiceId,
    string EndpointId,
    string ServiceSlug,
    string HttpMethod,
    string PathTemplate,
    IReadOnlyList<WorkflowCapabilityParameterHttpResponse> Parameters,
    WorkflowCapabilityRequestBodyHttpResponse? RequestBody,
    WorkflowCapabilityResponsePolicyHttpResponse? ResponsePolicy,
    WorkflowCapabilityExecutionPolicyHttpResponse? ExecutionPolicy);

public sealed record WorkflowCapabilityParameterHttpResponse(
    string Name,
    string Location,
    bool Required,
    WorkflowCapabilitySchemaHttpResponse Schema);

public sealed record WorkflowCapabilityRequestBodyHttpResponse(
    bool Required,
    string MediaType,
    WorkflowCapabilitySchemaHttpResponse Schema);

public sealed record WorkflowCapabilitySchemaPropertyHttpResponse(
    string Name,
    WorkflowCapabilitySchemaHttpResponse Schema);

public sealed record WorkflowCapabilitySchemaHttpResponse(
    string ValueKind,
    IReadOnlyList<WorkflowCapabilitySchemaPropertyHttpResponse> Properties,
    IReadOnlyList<string> RequiredProperties,
    WorkflowCapabilitySchemaHttpResponse? Items,
    IReadOnlyList<string> AllowedValues,
    bool AdditionalPropertiesAllowed);

public sealed record WorkflowCapabilityResponsePolicyHttpResponse(
    bool TextAllowed,
    bool FileArtifactAllowed,
    IReadOnlyList<string> MediaTypes);

public sealed record WorkflowCapabilityExecutionPolicyHttpResponse(
    string Risk,
    string Approval,
    string EnforcementOwner,
    IReadOnlyList<string> AllowedExecutionModes);

internal static class WorkflowCapabilityHttpContracts
{
    public static WorkflowCapabilityListHttpResponse ToHttpResponse(
        ExternalWorkflowCapabilityDiscoveryResult result) =>
        new(
            result.Capabilities.Select(ToDescriptor).ToArray(),
            result.CandidateCount,
            result.RejectedCount,
            result.Diagnostics.Select(ToDiagnostic).ToArray());

    public static WorkflowCapabilityReadinessHttpResponse ToHttpResponse(
        ExternalCapabilityReadiness readiness) =>
        new(
            ToWireValue(readiness.ExecutionMode),
            ToWireValue(readiness.Status),
            readiness.SelectedSelector?.SelectorCase is null or ExternalWorkflowCapabilitySelector.SelectorOneofCase.None
                ? null
                : ToSelector(readiness.SelectedSelector),
            readiness.SelectedCapability?.CapabilityCase == ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService
                ? ToOperation(readiness.SelectedCapability.NyxIdUserService)
                : null,
            readiness.Blockers.Select(static blocker => new WorkflowCapabilityBlockerHttpResponse(
                ToWireValue(blocker.Status),
                blocker.Code,
                blocker.SafeMessage)).ToArray(),
            readiness.Remediations.Select(static remediation => new WorkflowCapabilityRemediationHttpResponse(
                ToWireValue(remediation.ActionKind),
                remediation.Label,
                remediation.TrustedLocator)).ToArray(),
            readiness.Sources.Select(ToSource).ToArray());

    public static ExternalWorkflowCapabilitySelector ToSelector(
        WorkflowCapabilitySelectorHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Kind?.Trim(), "nyxid_operation", StringComparison.Ordinal))
            throw new InvalidOperationException("Selector kind must be 'nyxid_operation'.");

        return new ExternalWorkflowCapabilitySelector
        {
            NyxIdOperation = new NyxIdOperationSelector
            {
                UserServiceId = NormalizeRequired(request.UserServiceId, "Selector.UserServiceId"),
                EndpointId = NormalizeRequired(request.EndpointId, "Selector.EndpointId"),
            },
        };
    }

    private static WorkflowCapabilityDescriptorHttpResponse ToDescriptor(
        ExternalWorkflowCapabilityDescriptor descriptor) =>
        new(
            descriptor.DisplayName,
            descriptor.ReadOnly,
            descriptor.Destructive,
            ToSelector(descriptor.Selector),
            descriptor.Source is null ? null : ToSource(descriptor.Source));

    private static WorkflowCapabilitySelectorHttpResponse ToSelector(
        ExternalWorkflowCapabilitySelector selector) => selector.SelectorCase switch
        {
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.HostConnector => new(
                "host_connector",
                ConnectorCapabilityRef: selector.HostConnector.ConnectorCapabilityRef,
                OperationId: selector.HostConnector.OperationId,
                ContractDigest: selector.HostConnector.ContractDigest),
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdOperation => new(
                "nyxid_operation",
                UserServiceId: selector.NyxIdOperation.UserServiceId,
                EndpointId: selector.NyxIdOperation.EndpointId),
            ExternalWorkflowCapabilitySelector.SelectorOneofCase.NyxIdRequest => new(
                "nyxid_request",
                UserServiceId: selector.NyxIdRequest.UserServiceId,
                Method: ToWireValue(selector.NyxIdRequest.Method),
                PathTemplate: selector.NyxIdRequest.PathTemplate,
                QueryParameters: selector.NyxIdRequest.QueryParameters.ToArray(),
                HeaderParameters: selector.NyxIdRequest.HeaderParameters.ToArray(),
                BodyMode: ToWireValue(selector.NyxIdRequest.BodyMode),
                ResponseMode: ToWireValue(selector.NyxIdRequest.ResponseMode),
                BodyRequired: selector.NyxIdRequest.BodyRequired),
            _ => throw new InvalidOperationException("External capability selector is required."),
        };

    private static WorkflowCapabilitySourceHttpResponse ToSource(
        ExternalCapabilitySourceStamp source) =>
        new(
            ToWireValue(source.SourceKind),
            source.SourceId,
            source.SourceVersion,
            source.ObservedAt?.ToDateTimeOffset(),
            source.FreshUntil?.ToDateTimeOffset());

    private static WorkflowCapabilityDiagnosticHttpResponse ToDiagnostic(
        ExternalCapabilityDiscoveryDiagnostic diagnostic) =>
        new(
            ToWireValue(diagnostic.Code),
            diagnostic.SafeMessage,
            diagnostic.Count,
            diagnostic.Source is null ? null : ToSource(diagnostic.Source));

    private static WorkflowCapabilityOperationHttpResponse ToOperation(
        NyxIdUserServiceCapabilityRef operation) =>
        new(
            operation.UserServiceId,
            operation.EndpointId,
            operation.ServiceSlugSnapshot,
            operation.HttpMethod,
            operation.PathTemplate,
            operation.Parameters.Select(static parameter => new WorkflowCapabilityParameterHttpResponse(
                parameter.Name,
                ToWireValue(parameter.Location),
                parameter.Required,
                ToSchema(parameter.Schema))).ToArray(),
            operation.RequestBody is null
                ? null
                : new WorkflowCapabilityRequestBodyHttpResponse(
                    operation.RequestBody.Required,
                    operation.RequestBody.MediaType,
                    ToSchema(operation.RequestBody.Schema)),
            operation.ResponsePolicy is null
                ? null
                : new WorkflowCapabilityResponsePolicyHttpResponse(
                    operation.ResponsePolicy.TextAllowed,
                    operation.ResponsePolicy.FileArtifactAllowed,
                    operation.ResponsePolicy.MediaTypes.ToArray()),
            operation.ExecutionPolicy is null
                ? null
                : new WorkflowCapabilityExecutionPolicyHttpResponse(
                    ToWireValue(operation.ExecutionPolicy.Risk),
                    ToWireValue(operation.ExecutionPolicy.Approval),
                    ToWireValue(operation.ExecutionPolicy.EnforcementOwner),
                    operation.ExecutionPolicy.AllowedExecutionModes.Select(ToWireValue).ToArray()));

    private static WorkflowCapabilitySchemaHttpResponse ToSchema(NyxIdOperationSchema? schema)
    {
        if (schema is null)
            throw new InvalidOperationException("External capability input schema is required.");

        return new WorkflowCapabilitySchemaHttpResponse(
            ToWireValue(schema.ValueKind),
            schema.Properties.Select(static property => new WorkflowCapabilitySchemaPropertyHttpResponse(
                property.Name,
                ToSchema(property.Schema))).ToArray(),
            schema.RequiredProperties.ToArray(),
            schema.Items is null ? null : ToSchema(schema.Items),
            schema.AllowedValues.ToArray(),
            schema.AdditionalPropertiesAllowed);
    }

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{name} is required.");

        return normalized;
    }

    private static string ToWireValue(ExternalCapabilityExecutionMode value) => value switch
    {
        ExternalCapabilityExecutionMode.Interactive => "interactive",
        ExternalCapabilityExecutionMode.Durable => "durable",
        _ => "unspecified",
    };

    private static string ToWireValue(ExternalCapabilityReadinessStatus value) => value switch
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

    private static string ToWireValue(ExternalCapabilityRemediationActionKind value) => value switch
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

    private static string ToWireValue(ExternalCapabilitySourceKind value) => value switch
    {
        ExternalCapabilitySourceKind.ConnectorCatalog => "connector_catalog",
        ExternalCapabilitySourceKind.NyxIdUserServices => "nyxid_user_services",
        ExternalCapabilitySourceKind.NyxIdOpenApi => "nyxid_open_api",
        ExternalCapabilitySourceKind.DurableAuthorizationCatalog => "durable_authorization_catalog",
        ExternalCapabilitySourceKind.NyxIdMcpConfig => "nyxid_mcp_config",
        _ => "unspecified",
    };

    private static string ToWireValue(ExternalCapabilityDiscoveryDiagnosticCode value) => value switch
    {
        ExternalCapabilityDiscoveryDiagnosticCode.SourceUnavailable => "source_unavailable",
        ExternalCapabilityDiscoveryDiagnosticCode.NoExactUserService => "no_exact_user_service",
        ExternalCapabilityDiscoveryDiagnosticCode.GenericProxyRejected => "generic_proxy_rejected",
        ExternalCapabilityDiscoveryDiagnosticCode.InvalidServiceIdentity => "invalid_service_identity",
        ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousServiceIdentity => "ambiguous_service_identity",
        ExternalCapabilityDiscoveryDiagnosticCode.InvalidEndpointIdentity => "invalid_endpoint_identity",
        ExternalCapabilityDiscoveryDiagnosticCode.AmbiguousEndpointIdentity => "ambiguous_endpoint_identity",
        ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedParameter => "unsupported_parameter",
        ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedRequestBody => "unsupported_request_body",
        ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedSchema => "unsupported_schema",
        ExternalCapabilityDiscoveryDiagnosticCode.UnsupportedResponse => "unsupported_response",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdRequestMethod value) => value switch
    {
        NyxIdRequestMethod.Get => "get",
        NyxIdRequestMethod.Head => "head",
        NyxIdRequestMethod.Options => "options",
        NyxIdRequestMethod.Post => "post",
        NyxIdRequestMethod.Put => "put",
        NyxIdRequestMethod.Patch => "patch",
        NyxIdRequestMethod.Delete => "delete",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdRequestBodyMode value) => value switch
    {
        NyxIdRequestBodyMode.None => "none",
        NyxIdRequestBodyMode.Json => "json",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdRequestResponseMode value) => value switch
    {
        NyxIdRequestResponseMode.Text => "text",
        NyxIdRequestResponseMode.FileArtifact => "file_artifact",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdOperationParameterLocation value) => value switch
    {
        NyxIdOperationParameterLocation.Path => "path",
        NyxIdOperationParameterLocation.Query => "query",
        NyxIdOperationParameterLocation.Header => "header",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdOperationValueKind value) => value switch
    {
        NyxIdOperationValueKind.String => "string",
        NyxIdOperationValueKind.Integer => "integer",
        NyxIdOperationValueKind.Number => "number",
        NyxIdOperationValueKind.Boolean => "boolean",
        NyxIdOperationValueKind.Object => "object",
        NyxIdOperationValueKind.Array => "array",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdOperationRisk value) => value switch
    {
        NyxIdOperationRisk.ReadOnly => "read_only",
        NyxIdOperationRisk.Write => "write",
        NyxIdOperationRisk.Destructive => "destructive",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdOperationApproval value) => value switch
    {
        NyxIdOperationApproval.None => "none",
        NyxIdOperationApproval.Required => "required",
        _ => "unspecified",
    };

    private static string ToWireValue(NyxIdOperationEnforcementOwner value) => value switch
    {
        NyxIdOperationEnforcementOwner.Aevatar => "aevatar",
        NyxIdOperationEnforcementOwner.NyxId => "nyxid",
        _ => "unspecified",
    };
}
