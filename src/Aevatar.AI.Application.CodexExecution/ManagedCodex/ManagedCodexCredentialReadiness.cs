using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.AI.Application.CodexExecution;

public sealed record ManagedCodexCredentialReadinessAssessment(
    bool ExecutionReady,
    string Reason,
    string Message);

public static class ManagedCodexCredentialReadiness
{
    public static ManagedCodexCredentialReadinessAssessment Assess(
        ManagedCodexOptions options,
        ExternalSubjectRef owner,
        ManagedCodexCredentialSnapshot? snapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(owner);

        if (!options.Enabled)
        {
            return NotReady(
                "managed_target_disabled",
                "Managed Codex execution is disabled.");
        }

        if (options.Eligibility is null || !options.IsEligible(owner.ExternalUserId))
        {
            return NotReady(
                "managed_feature_not_enabled",
                "Managed Codex execution is not enabled for this user.");
        }

        return AssessCredential(owner, snapshot?.Credential, now);
    }

    internal static ManagedCodexCredentialReadinessAssessment AssessCredential(
        ExternalSubjectRef owner,
        ManagedCodexCredentialDescriptor? credential,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (credential is null)
        {
            return NotReady(
                "managed_credential_not_provisioned",
                "Managed Codex credential is not provisioned; use the credential lifecycle endpoint.");
        }

        if (credential.Status != ManagedCodexCredentialStatus.Active)
        {
            return NotReady(
                "managed_credential_inactive",
                "Managed Codex credential is inactive; reconcile or rotate it explicitly.");
        }

        if (!TryGetFutureExpiry(credential.ExpiresAt, now, out var expiresAt))
        {
            return NotReady(
                "managed_credential_expired",
                "Managed Codex credential is expired; rotate it explicitly.");
        }

        if (!TryGetMatchingOwnerScopeKey(owner, credential.Owner, out var ownerScopeKey))
        {
            return NotReady(
                "managed_credential_owner_invalid",
                "Managed Codex credential owner is invalid; reconcile it explicitly.");
        }

        if (!ReferenceMatches(credential.SecretReference, ownerScopeKey, expiresAt))
        {
            return NotReady(
                "managed_credential_reference_invalid",
                "Managed Codex credential reference is invalid; reconcile it explicitly.");
        }

        if (!ServiceBindingMatches(credential))
        {
            return NotReady(
                "managed_credential_service_binding_invalid",
                "Managed Codex service binding is invalid; reconcile it explicitly.");
        }

        return new ManagedCodexCredentialReadinessAssessment(
            true,
            "ready",
            "Managed Codex credential is ready for execution.");
    }

    private static bool TryGetFutureExpiry(
        Google.Protobuf.WellKnownTypes.Timestamp? timestamp,
        DateTimeOffset now,
        out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (timestamp is null)
            return false;

        try
        {
            expiresAt = timestamp.ToDateTimeOffset();
            return expiresAt > now;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryGetMatchingOwnerScopeKey(
        ExternalSubjectRef expectedOwner,
        ExternalSubjectRef? credentialOwner,
        out string ownerScopeKey)
    {
        ownerScopeKey = string.Empty;
        if (credentialOwner is null)
            return false;

        try
        {
            ownerScopeKey = ManagedCodexCredentialActorIdentity.From(expectedOwner);
            var credentialOwnerScopeKey =
                ManagedCodexCredentialActorIdentity.From(credentialOwner);
            return string.Equals(
                credentialOwnerScopeKey,
                ownerScopeKey,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ReferenceMatches(
        SecretReference? reference,
        string ownerScopeKey,
        DateTimeOffset expiresAt) =>
        reference is not null &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
            StringComparison.Ordinal) &&
        string.Equals(
            reference.OwnerScopeKey,
            ownerScopeKey,
            StringComparison.Ordinal) &&
        reference.Version > 0 &&
        !string.IsNullOrWhiteSpace(reference.Fingerprint) &&
        reference.ExpiresAtUnixMs == expiresAt.ToUnixTimeMilliseconds();

    private static bool ServiceBindingMatches(
        ManagedCodexCredentialDescriptor credential) =>
        !string.IsNullOrWhiteSpace(credential.ApiKeyId) &&
        !string.IsNullOrWhiteSpace(credential.ChronoSandboxUserServiceId) &&
        !string.IsNullOrWhiteSpace(credential.ChronoLlmUserServiceId) &&
        !string.Equals(
            credential.ChronoSandboxUserServiceId,
            credential.ChronoLlmUserServiceId,
            StringComparison.Ordinal) &&
        string.Equals(
            credential.ChronoSandboxServiceSlug,
            ManagedCodexOptions.ChronoSandboxServiceSlug,
            StringComparison.Ordinal);

    private static ManagedCodexCredentialReadinessAssessment NotReady(
        string reason,
        string message) =>
        new(false, reason, message);
}
