using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Core.Runtime;

/// <summary>
/// Runtime-neutral validation for a live fleet capability admission proof.
/// Both schema migration and post-adoption writes use this single policy so
/// membership and freshness checks cannot drift between the two boundaries.
/// </summary>
public static class RuntimeFleetCapabilityAdmissionValidation
{
    public static async Task<bool> IsGrantedAsync(
        RuntimeFleetCapability requiredCapability,
        string requiredContractId,
        int requiredReaderContractVersion,
        IRuntimeFleetCapabilityAdmissionReader admissionReader,
        IRuntimeLocalMembershipIdentityReader membershipReader,
        TimeProvider? timeProvider = null,
        RuntimeActorStateMigrationAdmissionOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admissionReader);
        ArgumentNullException.ThrowIfNull(membershipReader);
        ValidateRequirement(requiredCapability, requiredContractId, requiredReaderContractVersion);
        ct.ThrowIfCancellationRequested();

        var localMembership = await membershipReader.GetCurrentAsync(ct);
        if (!IsValidLocalMembership(localMembership))
            return false;

        var admission = await admissionReader.GetAsync(requiredCapability, ct);
        return IsGranted(
            admission,
            requiredCapability,
            requiredContractId,
            requiredReaderContractVersion,
            localMembership!,
            (timeProvider ?? TimeProvider.System).GetUtcNow(),
            options ?? new RuntimeActorStateMigrationAdmissionOptions());
    }

    internal static bool IsValidLocalMembership(RuntimeLocalMembershipIdentity? membership) =>
        membership is { MembershipEpoch: > 0 } &&
        !string.IsNullOrWhiteSpace(membership.MembershipDigest) &&
        !string.IsNullOrWhiteSpace(membership.DeploymentRevision) &&
        !string.IsNullOrWhiteSpace(membership.LocalMemberId) &&
        !string.IsNullOrWhiteSpace(membership.LocalMemberIncarnation);

    internal static bool IsGranted(
        RuntimeFleetCapabilityAdmission? admission,
        RuntimeFleetCapability requiredCapability,
        string requiredContractId,
        int requiredReaderContractVersion,
        RuntimeLocalMembershipIdentity localMembership,
        DateTimeOffset now,
        RuntimeActorStateMigrationAdmissionOptions options)
    {
        ArgumentNullException.ThrowIfNull(localMembership);
        ArgumentNullException.ThrowIfNull(options);
        ValidateRequirement(requiredCapability, requiredContractId, requiredReaderContractVersion);

        if (admission == null ||
            admission.Capability != requiredCapability ||
            admission.Status != RuntimeFleetCapabilityGateStatus.Open ||
            !string.Equals(
                admission.AuthorityActorId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal) ||
            admission.AuthorityStateVersion <= 0 ||
            admission.CapabilityEpoch <= 0 ||
            admission.MembershipEpoch != localMembership.MembershipEpoch ||
            !string.Equals(
                admission.MembershipDigest,
                localMembership.MembershipDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                admission.DeploymentRevision,
                localMembership.DeploymentRevision,
                StringComparison.Ordinal) ||
            !string.Equals(admission.ContractId, requiredContractId, StringComparison.Ordinal) ||
            admission.MinimumReaderContractVersion < requiredReaderContractVersion ||
            admission.ActiveMemberCount <= 0 ||
            admission.ConfirmedMemberCount != admission.ActiveMemberCount ||
            admission.MembershipObservedAt == null ||
            admission.MembershipValidUntil == null ||
            !ContainsExactLocalMember(admission, localMembership))
        {
            return false;
        }

        var observedAt = admission.MembershipObservedAt.ToDateTimeOffset();
        var validUntil = admission.MembershipValidUntil.ToDateTimeOffset();
        return observedAt <= now + options.MaxClockSkew &&
               validUntil > now &&
               validUntil > observedAt &&
               validUntil <= observedAt + options.MaxMembershipEvidenceTtl;
    }

    private static bool ContainsExactLocalMember(
        RuntimeFleetCapabilityAdmission admission,
        RuntimeLocalMembershipIdentity localMembership)
    {
        if (admission.AdmittedMembers.Count != admission.ActiveMemberCount)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var localMatches = 0;
        foreach (var member in admission.AdmittedMembers)
        {
            if (string.IsNullOrWhiteSpace(member.MemberId) ||
                string.IsNullOrWhiteSpace(member.Incarnation) ||
                !seen.Add(member.MemberId))
            {
                return false;
            }

            if (string.Equals(member.MemberId, localMembership.LocalMemberId, StringComparison.Ordinal) &&
                string.Equals(
                    member.Incarnation,
                    localMembership.LocalMemberIncarnation,
                    StringComparison.Ordinal))
            {
                localMatches++;
            }
        }

        return localMatches == 1;
    }

    private static void ValidateRequirement(
        RuntimeFleetCapability requiredCapability,
        string requiredContractId,
        int requiredReaderContractVersion)
    {
        if (requiredCapability == RuntimeFleetCapability.Unspecified ||
            !System.Enum.IsDefined(requiredCapability) ||
            string.IsNullOrWhiteSpace(requiredContractId) ||
            requiredReaderContractVersion <= 0)
        {
            throw new ArgumentException("A live fleet admission check requires an exact versioned capability contract.");
        }
    }
}
