using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Tools;

namespace Aevatar.GAgents.NyxidChat;

internal static class NyxIdChatOperationAdmissionPolicy
{
    private const int Sha256HexLength = 64;

    public static bool IsConnectedServiceCall(NyxIdChatToolCall call) =>
        call.OperationAdmission is not null || call.NyxIdProvenance is not null;

    public static bool IsValid(
        AgentToolOperationAdmissionPayload? admission,
        NyxIdChatToolCallSafety? safety,
        NyxIdOperationRef? provenance = null)
    {
        if (admission is null ||
            safety is null ||
            admission.IdentityCase !=
            AgentToolOperationAdmissionPayload.IdentityOneofCase.PublishedEndpoint ||
            admission.AuthorizationBasis !=
            AgentToolOperationAuthorizationBasisPayload.PublishedContract ||
            string.IsNullOrWhiteSpace(admission.ServiceInstanceId) ||
            string.IsNullOrWhiteSpace(admission.ServiceSlug) ||
            string.IsNullOrWhiteSpace(admission.PublishedEndpoint?.EndpointId) ||
            string.IsNullOrWhiteSpace(admission.HttpMethod) ||
            string.IsNullOrWhiteSpace(admission.PathTemplate) ||
            string.IsNullOrWhiteSpace(admission.ContractDigest) ||
            !IsCatalogDigest(admission.CatalogDigest) ||
            admission.ExecutionPolicy is null ||
            admission.ExecutionPolicy.EnforcementOwner !=
            AgentToolOperationEnforcementOwnerPayload.Aevatar ||
            !admission.ExecutionPolicy.AllowedExecutionModes.Contains(
                AgentToolOperationExecutionModePayload.Interactive))
        {
            return false;
        }

        if (provenance is not null &&
            (!string.Equals(
                 provenance.ConnectedServiceId,
                 admission.ServiceInstanceId,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 provenance.ServiceSlug,
                 admission.ServiceSlug,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 provenance.OperationId,
                 admission.PublishedEndpoint.EndpointId,
                 StringComparison.Ordinal)))
        {
            return false;
        }

        return admission.ExecutionPolicy.Risk switch
        {
            AgentToolOperationRiskPayload.ReadOnly =>
                admission.HttpMethod is "GET" or "HEAD" or "OPTIONS" &&
                admission.ExecutionPolicy.Approval == AgentToolOperationApprovalPayload.None &&
                safety.IsReadOnly &&
                !safety.IsDestructive &&
                !safety.MayChangeExternalState,
            AgentToolOperationRiskPayload.Write =>
                admission.HttpMethod is "POST" or "PUT" or "PATCH" &&
                admission.ExecutionPolicy.Approval == AgentToolOperationApprovalPayload.Required &&
                !safety.IsReadOnly &&
                !safety.IsDestructive &&
                safety.MayChangeExternalState,
            _ => false,
        };
    }

    public static bool Matches(
        AgentToolOperationAdmissionPayload? expected,
        AgentToolOperationAdmissionPayload? actual) =>
        expected is null
            ? actual is null
            : actual is not null && expected.Equals(actual);

    private static bool IsCatalogDigest(string? value)
    {
        const string prefix = "sha256:";
        if (value is null ||
            value.Length != prefix.Length + Sha256HexLength ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }
}
