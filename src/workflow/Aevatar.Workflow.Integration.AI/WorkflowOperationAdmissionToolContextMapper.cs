using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Integration.AI;

/// <summary>
/// Projects the workflow-owned call-site proof onto the provider-neutral tool context contract.
/// The AI layer never depends on workflow types, so the boundary maps here and nowhere else.
/// </summary>
public static class WorkflowOperationAdmissionToolContextMapper
{
    public static AgentToolOperationAdmission? Map(ExternalWorkflowCapabilityRef? capability)
    {
        if (capability?.CapabilityCase !=
            ExternalWorkflowCapabilityRef.CapabilityOneofCase.NyxIdUserService)
        {
            return null;
        }

        var proof = capability.NyxIdUserService;
        return new AgentToolOperationAdmission(
            proof.UserServiceId,
            proof.ServiceSlugSnapshot,
            proof.EndpointId,
            proof.HttpMethod,
            proof.PathTemplate,
            proof.ContractDigest,
            proof.Parameters.Select(MapParameter).ToArray(),
            MapRequestBody(proof.RequestBody),
            MapResponsePolicy(proof.ResponsePolicy),
            MapExecutionPolicy(proof.ExecutionPolicy));
    }

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
