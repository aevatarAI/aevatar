using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.Channel.Runtime;

internal sealed class ChannelRegistrationAuthorityAdmissionPort(
    IChannelBotRegistrationQueryByNyxIdentityPort registrationQuery,
    IChannelRegistrationCallSiteDependencyResolver dependencyResolver)
    : IChannelRegistrationAuthorityAdmissionPort
{
    private readonly IChannelBotRegistrationQueryByNyxIdentityPort _registrationQuery =
        registrationQuery ?? throw new ArgumentNullException(nameof(registrationQuery));
    private readonly IChannelRegistrationCallSiteDependencyResolver _dependencyResolver =
        dependencyResolver ?? throw new ArgumentNullException(nameof(dependencyResolver));

    public async Task<ChannelRegistrationAuthorityAdmissionResult> AdmitAsync(
        ChannelRegistrationAuthorityAdmissionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Credential is null)
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.CredentialDescriptorMismatch);
        }

        if (request.Operation is null)
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.OperationAdmissionMissing);
        }

        var registrationSnapshots = await _registrationQuery.ListSnapshotsByNyxAgentApiKeyIdAsync(
            request.Credential.SubjectId,
            ct).ConfigureAwait(false);
        if (registrationSnapshots is null)
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.AuthorityUnavailable);
        }

        if (registrationSnapshots.Count == 0)
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.RegistrationMissing);
        }

        if (registrationSnapshots.Count != 1)
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.RegistrationAmbiguous);
        }

        var registrationSnapshot = registrationSnapshots[0];
        if (registrationSnapshot.StateVersion <= 0)
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.AuthorityUnavailable);
        }

        var registration = registrationSnapshot.Registration;
        if (!MatchesCredentialDescriptor(registration, request.Credential))
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.CredentialDescriptorMismatch);
        }

        var contractKind = ChannelRegistrationAuthorizationContract.Classify(registration);
        if (contractKind == ChannelRegistrationAuthorizationContractKind.Invalid)
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.AuthorizationContractInvalid);
        }

        if (contractKind is ChannelRegistrationAuthorizationContractKind.NyxIdDefault or
            ChannelRegistrationAuthorizationContractKind.HistoricalLegacy)
        {
            return ChannelRegistrationAuthorityAdmissionResult.Allow();
        }

        var targetServiceInstanceId = request.Operation.ServiceInstanceId;
        // Agent Key connected-service tools use slug-only NyxID proxy routes, so their operation
        // target is synthetic. Admission binds those operations to the registration-sealed runtime selector.
        if (TryAdmitSyntheticAgentKeyService(registration, request.Operation, targetServiceInstanceId, out var syntheticResult))
            return syntheticResult;

        if (!IsCanonicalIdentifier(targetServiceInstanceId) ||
            !GrantAllowsService(registration.ChannelAgentKey.Grant, targetServiceInstanceId))
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.TargetNotGranted);
        }

        if (IsConnectedServiceOperation(request.Operation))
        {
            return RuntimeSelectorsAllow(registration.RuntimeConfig, request.Operation, allowWhenNoSelectors: true)
                ? ChannelRegistrationAuthorityAdmissionResult.Allow()
                : ChannelRegistrationAuthorityAdmissionResult.Deny(
                    ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized);
        }

        if (ContainsExact(
                registration.RegistrationServiceAllowlist.ServiceIds,
                targetServiceInstanceId))
        {
            return ChannelRegistrationAuthorityAdmissionResult.Allow();
        }

        if (!IsCanonicalIdentifier(request.Operation.CallSiteId))
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized);
        }

        var dependencyAuthorized = await _dependencyResolver.IsAuthorizedDependencyAsync(
            registration.ScopeId,
            request.Operation.CallSiteId,
            targetServiceInstanceId,
            ct).ConfigureAwait(false);
        return dependencyAuthorized
            ? ChannelRegistrationAuthorityAdmissionResult.Allow()
            : ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized);
    }

    private static bool TryAdmitSyntheticAgentKeyService(
        ChannelBotRegistrationEntry registration,
        AgentToolOperationAdmission operation,
        string targetServiceInstanceId,
        out ChannelRegistrationAuthorityAdmissionResult result)
    {
        result = ChannelRegistrationAuthorityAdmissionResult.Deny(
            ChannelRegistrationAuthorityAdmissionReason.TargetNotGranted);
        if (!TryReadAgentKeySyntheticServiceSlug(targetServiceInstanceId, out var serviceSlug))
            return false;

        if (!string.Equals(operation.ServiceSlug, serviceSlug, StringComparison.OrdinalIgnoreCase))
            return true;

        if (registration.RegistrationServiceAllowlist.ServiceIds.Count == 0)
            return true;

        if (!RuntimeSelectorsAllow(registration.RuntimeConfig, operation, allowWhenNoSelectors: false))
        {
            result = ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.TargetNotAuthorized);
            return true;
        }

        result = ChannelRegistrationAuthorityAdmissionResult.Allow();
        return true;
    }

    private static bool RuntimeSelectorsAllow(
        ChannelBotRuntimeConfig? runtimeConfig,
        AgentToolOperationAdmission operation,
        bool allowWhenNoSelectors)
    {
        if (runtimeConfig?.NyxidServiceSelectors.Count is null or 0)
            return allowWhenNoSelectors;

        return runtimeConfig.NyxidServiceSelectors.Any(selector =>
            SelectorAllowsOperation(selector, operation));
    }

    private static bool SelectorAllowsOperation(
        ChannelBotRuntimeNyxIdServiceSelector selector,
        AgentToolOperationAdmission operation)
    {
        if (!MatchesSelector(selector, operation))
            return false;
        if (selector.EndpointNames.Count == 0)
            return true;
        return operation.Identity is AgentToolOperationIdentity.PublishedEndpoint published &&
               selector.EndpointNames.Any(endpointName =>
                   string.Equals(endpointName, published.EndpointId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSelector(
        ChannelBotRuntimeNyxIdServiceSelector selector,
        AgentToolOperationAdmission operation) =>
        string.Equals(selector.ServiceSlug, operation.ServiceSlug, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(selector.ServiceSlug, operation.CatalogServiceSlug, StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectedServiceOperation(AgentToolOperationAdmission operation) =>
        !string.IsNullOrWhiteSpace(operation.CatalogServiceSlug);

    private static bool GrantAllowsService(
        ChannelAgentKeyGrantSnapshot grant,
        string serviceInstanceId) =>
        grant.AllowAllServices == true || ContainsExact(grant.AllowedServiceIds, serviceInstanceId);

    private static bool TryReadAgentKeySyntheticServiceSlug(
        string? serviceInstanceId,
        out string serviceSlug)
    {
        const string prefix = "agent-key:";
        serviceSlug = string.Empty;
        if (serviceInstanceId?.StartsWith(prefix, StringComparison.Ordinal) != true)
            return false;
        serviceSlug = serviceInstanceId[prefix.Length..];
        return IsCanonicalIdentifier(serviceSlug) &&
               !serviceSlug.Contains('/', StringComparison.Ordinal) &&
               !serviceSlug.Contains('\\', StringComparison.Ordinal);
    }

    private static bool MatchesCredentialDescriptor(
        ChannelBotRegistrationEntry registration,
        DurableCallerCredentialRef credential)
    {
        var historical = registration.AuthorizationMode ==
                         ChannelRegistrationAuthorizationMode.Unspecified &&
                         registration.ChannelAgentKey is null &&
                         registration.RegistrationServiceAllowlist is null;
        var expectedApiKeyId = historical
            ? registration.NyxAgentApiKeyId
            : registration.ChannelAgentKey?.ApiKeyId;
        var expectedReference = historical
            ? registration.WorkflowResultDeliveryCredential
            : registration.ChannelAgentKey?.SecretReference;
        if (expectedReference is null)
            return false;

        return credential.SourceKind == DurableCallerCredentialSourceKind.ChannelRegistration &&
               string.Equals(
                   credential.Purpose,
                   CredentialSecretPurposes.ChannelNyxIdAgentKey,
                   StringComparison.Ordinal) &&
               IsCanonicalIdentifier(registration.ScopeId) &&
               IsCanonicalIdentifier(expectedApiKeyId) &&
               string.Equals(credential.SubjectId, expectedApiKeyId, StringComparison.Ordinal) &&
               IsCompleteSecretReference(expectedReference, registration.ScopeId) &&
               string.Equals(credential.Ref, expectedReference.Ref, StringComparison.Ordinal) &&
               string.Equals(
                   credential.Purpose,
                   expectedReference.Purpose,
                   StringComparison.Ordinal) &&
               string.Equals(
                   credential.OwnerScopeKey,
                   expectedReference.OwnerScopeKey,
                   StringComparison.Ordinal) &&
               credential.SecretReference is not null &&
               credential.SecretReference.Equals(expectedReference);
    }

    private static bool IsCompleteSecretReference(
        SecretReference? reference,
        string scopeId) =>
        reference is not null &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(reference.OwnerScopeKey) &&
        string.Equals(reference.OwnerScopeKey, scopeId, StringComparison.Ordinal) &&
        reference.Version > 0 &&
        !string.IsNullOrWhiteSpace(reference.Fingerprint) &&
        reference.CreatedAtUnixMs > 0 &&
        reference.ExpiresAtUnixMs >= 0;

    private static bool IsCanonicalIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool ContainsExact(
        IEnumerable<string> values,
        string candidate) =>
        values.Any(value => string.Equals(value, candidate, StringComparison.Ordinal));
}
