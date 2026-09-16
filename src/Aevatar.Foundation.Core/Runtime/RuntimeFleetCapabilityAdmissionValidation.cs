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
        var grant = await GetGrantedAdmissionAsync(
            requiredCapability,
            requiredContractId,
            requiredReaderContractVersion,
            admissionReader,
            membershipReader,
            timeProvider,
            options,
            ct);
        return grant != null;
    }

    /// <summary>
    /// Reads and validates one fleet proof against one local membership
    /// identity, returning the exact validated snapshot for durable consumers.
    /// </summary>
    public static async Task<RuntimeFleetCapabilityAdmissionGrant?> GetGrantedAdmissionAsync(
        RuntimeFleetCapability requiredCapability,
        string requiredContractId,
        int requiredReaderContractVersion,
        IRuntimeFleetCapabilityAdmissionReader admissionReader,
        IRuntimeLocalMembershipIdentityReader membershipReader,
        TimeProvider? timeProvider = null,
        RuntimeActorStateMigrationAdmissionOptions? options = null,
        CancellationToken ct = default)
    {
        var validated = await ReadValidatedAdmissionAsync(
            requiredCapability,
            requiredContractId,
            requiredReaderContractVersion,
            admissionReader,
            membershipReader,
            timeProvider,
            options,
            ct);
        if (validated == null)
            return null;

        return new RuntimeFleetCapabilityAdmissionGrant(
            requiredCapability,
            validated.Admission,
            validated.LocalMembership,
            validated.ValidatedAt);
    }

    /// <summary>
    /// Validates a typed terminal quiescence marker. The evidence records the historical fleet
    /// that closed the gate and is deliberately independent of current membership; it is never an
    /// OPEN grant for a later rollout.
    /// </summary>
    public static async Task<RuntimeFleetCapabilityQuiescenceReceipt?> GetQuiescenceReceiptAsync(
        RuntimeFleetCapability requiredCapability,
        string requiredContractId,
        int requiredReaderContractVersion,
        IRuntimeFleetCapabilityQuiescenceReader quiescenceReader,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quiescenceReader);
        ValidateRequirement(requiredCapability, requiredContractId, requiredReaderContractVersion);
        ct.ThrowIfCancellationRequested();

        var evidence = (await quiescenceReader.GetQuiescenceAsync(requiredCapability, ct))?.Clone();
        if (evidence == null ||
            evidence.Capability != requiredCapability ||
            !string.Equals(
                evidence.AuthorityActorId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal) ||
            evidence.AuthorityStateVersion <= 0 ||
            evidence.CapabilityEpoch != long.MaxValue ||
            !string.Equals(evidence.ContractId, requiredContractId, StringComparison.Ordinal) ||
            evidence.QuiescenceReaderContractVersion < requiredReaderContractVersion ||
            evidence.QuiescedMembershipEpoch <= 0 ||
            string.IsNullOrWhiteSpace(evidence.QuiescedMembershipDigest) ||
            string.IsNullOrWhiteSpace(evidence.QuiescedDeploymentRevision) ||
            evidence.QuiescedAt == null ||
            string.IsNullOrWhiteSpace(evidence.QuiescenceTransitionId))
        {
            return null;
        }

        return new RuntimeFleetCapabilityQuiescenceReceipt(requiredCapability, evidence);
    }

    private static async Task<ValidatedAdmission?> ReadValidatedAdmissionAsync(
        RuntimeFleetCapability requiredCapability,
        string requiredContractId,
        int requiredReaderContractVersion,
        IRuntimeFleetCapabilityAdmissionReader admissionReader,
        IRuntimeLocalMembershipIdentityReader membershipReader,
        TimeProvider? timeProvider,
        RuntimeActorStateMigrationAdmissionOptions? options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admissionReader);
        ArgumentNullException.ThrowIfNull(membershipReader);
        ValidateRequirement(requiredCapability, requiredContractId, requiredReaderContractVersion);
        ct.ThrowIfCancellationRequested();

        var localMembership = await membershipReader.GetCurrentAsync(ct);
        if (!IsValidLocalMembership(localMembership))
            return null;

        var admission = (await admissionReader.GetAsync(requiredCapability, ct))?.Clone();
        var validatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return IsAdmission(
                admission,
                requiredCapability,
                requiredContractId,
                requiredReaderContractVersion,
                localMembership!,
                validatedAt,
                options ?? new RuntimeActorStateMigrationAdmissionOptions())
            ? new ValidatedAdmission(admission!, localMembership!, validatedAt)
            : null;
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

        return IsAdmission(
            admission,
            requiredCapability,
            requiredContractId,
            requiredReaderContractVersion,
            localMembership,
            now,
            options);
    }

    private static bool IsAdmission(
        RuntimeFleetCapabilityAdmission? admission,
        RuntimeFleetCapability requiredCapability,
        string requiredContractId,
        int requiredReaderContractVersion,
        RuntimeLocalMembershipIdentity localMembership,
        DateTimeOffset now,
        RuntimeActorStateMigrationAdmissionOptions options)
    {
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

    private sealed record ValidatedAdmission(
        RuntimeFleetCapabilityAdmission Admission,
        RuntimeLocalMembershipIdentity LocalMembership,
        DateTimeOffset ValidatedAt);

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

public sealed class RuntimeFleetCapabilityQuiescenceReceipt
{
    private readonly RuntimeFleetCapabilityQuiescenceEvidence _evidence;

    internal RuntimeFleetCapabilityQuiescenceReceipt(
        RuntimeFleetCapability capability,
        RuntimeFleetCapabilityQuiescenceEvidence evidence)
    {
        Capability = capability;
        _evidence = evidence.Clone();
    }

    public RuntimeFleetCapability Capability { get; }

    public RuntimeFleetCapabilityQuiescenceEvidence Evidence => _evidence.Clone();
}

/// <summary>
/// A point-in-time fleet capability grant produced from one admission and
/// local membership read. It is evidence for the validated instant, not a
/// perpetual grant after membership, authority, or freshness changes.
/// </summary>
public sealed class RuntimeFleetCapabilityAdmissionGrant
{
    private readonly RuntimeFleetCapabilityAdmission _admission;

    internal RuntimeFleetCapabilityAdmissionGrant(
        RuntimeFleetCapability capability,
        RuntimeFleetCapabilityAdmission admission,
        RuntimeLocalMembershipIdentity localMembership,
        DateTimeOffset validatedAt)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(localMembership);
        Capability = capability;
        _admission = admission;
        LocalMembership = localMembership;
        ValidatedAt = validatedAt;
    }

    public RuntimeFleetCapability Capability { get; }

    /// <summary>
    /// Returns a defensive copy of the proof that passed validation.
    /// </summary>
    public RuntimeFleetCapabilityAdmission Admission => _admission.Clone();

    public RuntimeLocalMembershipIdentity LocalMembership { get; }

    public DateTimeOffset ValidatedAt { get; }
}
