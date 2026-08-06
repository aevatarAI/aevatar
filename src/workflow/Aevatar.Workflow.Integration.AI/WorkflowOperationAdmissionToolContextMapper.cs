using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

/// <summary>
/// Projects the workflow-owned call-site proof onto the provider-neutral tool context contract.
/// The AI layer never depends on workflow types, so the boundary maps here and nowhere else.
/// </summary>
public static class WorkflowOperationAdmissionToolContextMapper
{
    public static AgentToolOperationAdmission? Map(WorkflowCapabilityInvocationAdmission? admission)
    {
        if (admission is null)
            return null;

        WorkflowCapabilityAdmissionPlanIntegrity
            .ValidateInvocationAdmissionIntrinsicIntegrity(admission);

        return admission.Capability.CapabilityCase switch
        {
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService =>
                MapPublished(admission),
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserRequest =>
                MapAuthored(admission),
            _ => null,
        };
    }

    private static AgentToolOperationAdmission MapPublished(
        WorkflowCapabilityInvocationAdmission admission)
    {
        var proof = admission.Capability.NyxIdUserService;
        return new AgentToolOperationAdmission(
            proof.UserServiceId,
            proof.ServiceSlugSnapshot,
            new AgentToolOperationIdentity.PublishedEndpoint(proof.EndpointId),
            AgentToolOperationAuthorizationBasis.PublishedContract,
            proof.HttpMethod,
            proof.PathTemplate,
            proof.ContractDigest,
            proof.Parameters.Select(MapParameter).ToArray(),
            MapRequestBody(proof.RequestBody),
            MapResponsePolicy(proof.ResponsePolicy),
            MapExecutionPolicy(proof.ExecutionPolicy));
    }

    private static AgentToolOperationAdmission MapAuthored(
        WorkflowCapabilityInvocationAdmission admission)
    {
        var proof = admission.Capability.NyxIdUserRequest;
        var request = proof.Request;
        var requestContractDigest = WorkflowCapabilityAdmissionPlanIntegrity
            .ComputeNyxIdRequestContractDigest(request);

        var parameters = NyxIdRequestSelectorContract.PathParameters(request)
            .Select(static name => new AgentToolOperationParameter(
                name,
                AgentToolOperationParameterLocation.Path,
                true,
                AgentToolOperationValueSchema.Text))
            .Concat(request.QueryParameters.Select(static name => new AgentToolOperationParameter(
                name,
                AgentToolOperationParameterLocation.Query,
                false,
                AgentToolOperationValueSchema.Text)))
            .Concat(request.HeaderParameters.Select(static name => new AgentToolOperationParameter(
                name,
                AgentToolOperationParameterLocation.Header,
                false,
                AgentToolOperationValueSchema.Text)))
            .ToArray();

        return new AgentToolOperationAdmission(
            request.UserServiceId,
            proof.ServiceSlugSnapshot,
            new AgentToolOperationIdentity.AuthoredRequest(requestContractDigest),
            AgentToolOperationAuthorizationBasis.ExplicitRequest,
            NyxIdRequestSelectorContract.MethodName(request.Method),
            request.PathTemplate,
            proof.ContractDigest,
            parameters,
            MapRequestBody(request),
            MapResponsePolicy(request.ResponseMode),
            MapExecutionPolicy(proof.ExecutionPolicy));
    }

    private static AgentToolOperationRequestBody? MapRequestBody(
        NyxIdRequestSelector request) =>
        request.BodyMode switch
        {
            NyxIdRequestBodyMode.None => null,
            NyxIdRequestBodyMode.Json => new AgentToolOperationRequestBody(
                request.BodyRequired,
                "application/json",
                new AgentToolOperationValueSchema(
                    AgentToolOperationValueKind.Object,
                    [],
                    new HashSet<string>(StringComparer.Ordinal),
                    null,
                    [],
                    true)),
            _ => throw new InvalidOperationException(
                "Workflow NyxID explicit request body mode is invalid."),
        };

    private static AgentToolOperationResponsePolicy MapResponsePolicy(
        NyxIdRequestResponseMode responseMode) =>
        responseMode switch
        {
            NyxIdRequestResponseMode.Text => AgentToolOperationResponsePolicy.TextOnly,
            NyxIdRequestResponseMode.FileArtifact =>
                new AgentToolOperationResponsePolicy(false, true, []),
            _ => throw new InvalidOperationException(
                "Workflow NyxID explicit request response mode is invalid."),
        };

    private static AgentToolOperationParameter MapParameter(NyxIdOperationParameterContract parameter) =>
        new(
            parameter.Name,
            parameter.Location switch
            {
                NyxIdOperationParameterLocation.Path => AgentToolOperationParameterLocation.Path,
                NyxIdOperationParameterLocation.Query => AgentToolOperationParameterLocation.Query,
                NyxIdOperationParameterLocation.Header => AgentToolOperationParameterLocation.Header,
                _ => AgentToolOperationParameterLocation.Unspecified,
            },
            parameter.Required,
            MapSchema(parameter.Schema));

    private static AgentToolOperationRequestBody? MapRequestBody(
        NyxIdOperationRequestBodyContract? requestBody) =>
        requestBody is null
            ? null
            : new AgentToolOperationRequestBody(
                requestBody.Required,
                requestBody.MediaType,
                MapSchema(requestBody.Schema));

    private static AgentToolOperationResponsePolicy MapResponsePolicy(
        NyxIdOperationResponsePolicy? responsePolicy) =>
        responsePolicy is null
            ? AgentToolOperationResponsePolicy.TextOnly
            : new AgentToolOperationResponsePolicy(
                responsePolicy.TextAllowed,
                responsePolicy.FileArtifactAllowed,
                responsePolicy.MediaTypes.ToArray());

    private static AgentToolOperationExecutionPolicy MapExecutionPolicy(
        NyxIdOperationExecutionPolicy? policy) =>
        policy is null
            ? AgentToolOperationExecutionPolicy.Unspecified
            : new AgentToolOperationExecutionPolicy(
                policy.Risk switch
                {
                    NyxIdOperationRisk.ReadOnly => AgentToolOperationRisk.ReadOnly,
                    NyxIdOperationRisk.Write => AgentToolOperationRisk.Write,
                    NyxIdOperationRisk.Destructive => AgentToolOperationRisk.Destructive,
                    _ => AgentToolOperationRisk.Unspecified,
                },
                policy.Approval switch
                {
                    NyxIdOperationApproval.None => AgentToolOperationApproval.None,
                    NyxIdOperationApproval.Required => AgentToolOperationApproval.Required,
                    _ => AgentToolOperationApproval.Unspecified,
                },
                policy.EnforcementOwner switch
                {
                    NyxIdOperationEnforcementOwner.Aevatar => AgentToolOperationEnforcementOwner.Aevatar,
                    NyxIdOperationEnforcementOwner.NyxId => AgentToolOperationEnforcementOwner.NyxId,
                    _ => AgentToolOperationEnforcementOwner.Unspecified,
                },
                policy.AllowedExecutionModes.Select(static mode => mode switch
                {
                    ExternalCapabilityExecutionMode.Interactive => AgentToolOperationExecutionMode.Interactive,
                    ExternalCapabilityExecutionMode.Durable => AgentToolOperationExecutionMode.Durable,
                    _ => AgentToolOperationExecutionMode.Unspecified,
                }).ToArray());

    private static AgentToolOperationValueSchema MapSchema(NyxIdOperationSchema? schema)
    {
        if (schema is null)
            return AgentToolOperationValueSchema.Text;

        return new AgentToolOperationValueSchema(
            schema.ValueKind switch
            {
                NyxIdOperationValueKind.String => AgentToolOperationValueKind.String,
                NyxIdOperationValueKind.Integer => AgentToolOperationValueKind.Integer,
                NyxIdOperationValueKind.Number => AgentToolOperationValueKind.Number,
                NyxIdOperationValueKind.Boolean => AgentToolOperationValueKind.Boolean,
                NyxIdOperationValueKind.Object => AgentToolOperationValueKind.Object,
                NyxIdOperationValueKind.Array => AgentToolOperationValueKind.Array,
                _ => AgentToolOperationValueKind.Unspecified,
            },
            schema.Properties
                .Select(property => new AgentToolOperationSchemaProperty(
                    property.Name,
                    MapSchema(property.Schema)))
                .ToArray(),
            new HashSet<string>(schema.RequiredProperties, StringComparer.Ordinal),
            schema.Items is null ? null : MapSchema(schema.Items),
            schema.AllowedValues.ToArray(),
            schema.AdditionalPropertiesAllowed);
    }
}
