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
        if (!IsCanonicalIdentifier(targetServiceInstanceId) ||
            !ContainsExact(
                registration.ChannelAgentKey.Grant.AllowedServiceIds,
                targetServiceInstanceId))
        {
            return ChannelRegistrationAuthorityAdmissionResult.Deny(
                ChannelRegistrationAuthorityAdmissionReason.TargetNotGranted);
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
