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
             !string.IsNullOrWhiteSpace(admission.CatalogServiceSlug) &&
             !string.Equals(
                 provenance.CatalogServiceSlug,
                 admission.CatalogServiceSlug,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 provenance.OperationId,
                 admission.PublishedEndpoint.EndpointId,
                 StringComparison.Ordinal)))
        {
            return false;
        }

        var validOperation = admission.ExecutionPolicy.Risk switch
        {
            AgentToolOperationRiskPayload.ReadOnly =>
                admission.HttpMethod is "GET" or "HEAD" or "OPTIONS" &&
                admission.ExecutionPolicy.Approval == AgentToolOperationApprovalPayload.None &&
                safety.IsReadOnly &&
                !safety.IsDestructive &&
                !safety.MayChangeExternalState &&
                admission.ReadBack is null,
            AgentToolOperationRiskPayload.Write =>
                admission.HttpMethod is "POST" or "PUT" or "PATCH" &&
                admission.ExecutionPolicy.Approval is
                    AgentToolOperationApprovalPayload.None or
                    AgentToolOperationApprovalPayload.Required &&
                !safety.IsReadOnly &&
                !safety.IsDestructive &&
                safety.MayChangeExternalState,
            _ => false,
        };
        return validOperation &&
               (admission.ReadBack is null || IsValidReadBack(admission.ReadBack, admission));
    }

    public static bool Matches(
        AgentToolOperationAdmissionPayload? expected,
        AgentToolOperationAdmissionPayload? actual) =>
        expected is null
            ? actual is null
            : actual is not null &&
              TryCanonicalize(expected, out var canonicalExpected) &&
              TryCanonicalize(actual, out var canonicalActual) &&
              canonicalExpected.Equals(canonicalActual);

    public static bool IsValidReadBack(
        AgentToolOperationReadBackPayload? readBack,
        AgentToolOperationAdmissionPayload? effectOperation = null)
    {
        if (readBack?.ReadOperation is not { } operation ||
            readBack.Arguments is null ||
            readBack.Assertion is null ||
            string.IsNullOrWhiteSpace(readBack.CheckName) ||
            readBack.Assertion.Match == AgentToolReadBackMatchPayload.Unspecified ||
            !AgentToolEffectResultIdentityJsonPointer.IsValid(
                readBack.EffectResultIdentityJsonPointer) ||
            operation.ReadBack is not null)
        {
            return false;
        }

        if (!AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryGetCanonicalSource(
                readBack.Assertion,
                out var expectedValueSource))
        {
            return false;
        }
        if (!string.IsNullOrEmpty(readBack.EffectResultIdentityJsonPointer) &&
            expectedValueSource !=
            AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId)
        {
            return false;
        }

        var notAppliedSource = AgentToolReadBackExpectedValueSourcePayload.Unspecified;
        if (readBack.NotAppliedAssertion is not null &&
            !AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryGetCanonicalSource(
                readBack.NotAppliedAssertion,
                out notAppliedSource))
        {
            return false;
        }

        var safety = new NyxIdChatToolCallSafety
        {
            IsReadOnly = true,
            MayChangeExternalState = false,
        };
        return IsValid(operation, safety) &&
               (effectOperation is null ||
                string.Equals(
                    operation.ServiceInstanceId,
                    effectOperation.ServiceInstanceId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.ServiceSlug,
                    effectOperation.ServiceSlug,
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.CatalogServiceSlug,
                    effectOperation.CatalogServiceSlug,
                    StringComparison.Ordinal) &&
                string.Equals(
                    operation.CatalogDigest,
                    effectOperation.CatalogDigest,
                    StringComparison.Ordinal)) &&
               IsValidAssertion(readBack.Assertion) &&
               IsValidProviderResourceArgument(
                   readBack.ProviderResourceArgument,
                   operation,
                   expectedValueSource) &&
               (readBack.Pagination is null ||
                readBack.Assertion.Match == AgentToolReadBackMatchPayload.ArrayContainsEquals) &&
               (readBack.NotAppliedAssertion is null ||
                readBack.NotAppliedAssertion.Match == AgentToolReadBackMatchPayload.Equals &&
                notAppliedSource == AgentToolReadBackExpectedValueSourcePayload.FrozenValue &&
                readBack.NotAppliedAssertion.ExpectedValue is not null &&
                !string.IsNullOrWhiteSpace(readBack.NotAppliedAssertion.JsonPointer)) &&
               IsValidPagination(readBack.Pagination, operation);
    }

    private static bool IsValidProviderResourceArgument(
        AgentToolReadBackProviderResourceArgumentPayload? argument,
        AgentToolOperationAdmissionPayload readOperation,
        AgentToolReadBackExpectedValueSourcePayload expectedValueSource) =>
        argument is null ||
        expectedValueSource == AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId &&
        (argument.Location is AgentToolOperationParameterLocationPayload.Path or
            AgentToolOperationParameterLocationPayload.Query or
            AgentToolOperationParameterLocationPayload.Header) &&
        !string.IsNullOrWhiteSpace(argument.ArgumentName) &&
        readOperation.Parameters.Any(parameter =>
            parameter.Location == argument.Location &&
            string.Equals(parameter.Name, argument.ArgumentName, StringComparison.Ordinal));

    private static bool IsValidAssertion(AgentToolReadBackAssertionPayload assertion) =>
        AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryGetCanonicalSource(
            assertion,
            out _) &&
        (assertion.Match != AgentToolReadBackMatchPayload.ArrayContainsEquals ||
         !string.IsNullOrWhiteSpace(assertion.JsonPointer) &&
         !string.IsNullOrWhiteSpace(assertion.ElementJsonPointer));

    private static bool TryCanonicalize(
        AgentToolOperationAdmissionPayload admission,
        out AgentToolOperationAdmissionPayload canonical)
    {
        canonical = admission.Clone();
        if (canonical.ReadBack is null)
            return true;

        if (!AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryCanonicalize(
                canonical.ReadBack.Assertion,
                out var assertion))
        {
            return false;
        }
        canonical.ReadBack.Assertion = assertion;

        if (canonical.ReadBack.NotAppliedAssertion is null)
            return true;
        if (!AgentToolReadBackExpectedValueSourcePayloadCanonicalizer.TryCanonicalize(
                canonical.ReadBack.NotAppliedAssertion,
                out var notAppliedAssertion))
        {
            return false;
        }
        canonical.ReadBack.NotAppliedAssertion = notAppliedAssertion;
        return true;
    }

    private static bool IsValidPagination(
        AgentToolReadBackPaginationPayload? pagination,
        AgentToolOperationAdmissionPayload readOperation) =>
        pagination is null ||
        pagination.MaxPages is > 0 and <= 1000 &&
        !string.IsNullOrWhiteSpace(pagination.HasMoreJsonPointer) &&
        !string.IsNullOrWhiteSpace(pagination.PageTokenJsonPointer) &&
        pagination.PageTokenLocation == AgentToolOperationParameterLocationPayload.Query &&
        !string.IsNullOrWhiteSpace(pagination.PageTokenArgumentName) &&
        readOperation.Parameters.Any(parameter =>
            parameter.Location == pagination.PageTokenLocation &&
            string.Equals(
                parameter.Name,
                pagination.PageTokenArgumentName,
                StringComparison.Ordinal));

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
