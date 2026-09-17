using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;

namespace Aevatar.Foundation.Projection.Runtime;

public sealed class ProjectionRuntimeFleetCapabilityAdmissionReader
    : IRuntimeFleetCapabilityAdmissionReader,
        IRuntimeFleetCapabilityQuiescenceReader
{
    private readonly IProjectionDocumentReader<
        RuntimeFleetCapabilityAuthorityCurrentStateDocument,
        string>? _documentReader;

    public ProjectionRuntimeFleetCapabilityAdmissionReader(
        IEnumerable<IProjectionDocumentReader<
            RuntimeFleetCapabilityAuthorityCurrentStateDocument,
            string>>? documentReaders)
    {
        var candidates = documentReaders?.Take(2).ToArray() ?? [];
        _documentReader = candidates.Length == 1 ? candidates[0] : null;
    }

    public async Task<RuntimeFleetCapabilityAdmission?> GetAsync(
        RuntimeFleetCapability capability,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_documentReader == null)
            return null;

        var document = await _documentReader.GetAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            ct);
        if (document == null ||
            !string.Equals(
                document.AuthorityActorId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal) ||
            document.StateVersion <= 0 ||
            document.Membership == null ||
            document.Membership.ActiveMembers.Count == 0)
        {
            return null;
        }

        var matchingGates = document.Gates
            .Where(gate => gate.Capability == capability)
            .ToArray();
        if (matchingGates.Length != 1)
            return null;
        var gate = matchingGates[0];
        var membership = document.Membership;
        var computedMembershipDigest = RuntimeFleetMembershipDigest.Compute(membership);
        if (!string.Equals(
                membership.MembershipDigest,
                computedMembershipDigest,
                StringComparison.Ordinal))
        {
            return null;
        }

        if (gate.Status != RuntimeFleetCapabilityGateStatus.Open ||
            gate.CapabilityEpoch <= 0 ||
            gate.MinimumReaderContractVersion <= 0 ||
            string.IsNullOrWhiteSpace(gate.RequiredContractId) ||
            gate.MembershipEpoch != membership.MembershipEpoch ||
            !string.Equals(
                gate.MembershipDigest,
                membership.MembershipDigest,
                StringComparison.Ordinal) ||
            !string.Equals(
                gate.DeploymentRevision,
                membership.DeploymentRevision,
                StringComparison.Ordinal) ||
            membership.ObservedAt == null ||
            membership.ValidUntil == null)
        {
            return null;
        }

        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        var confirmedMembers = 0;
        foreach (var member in membership.ActiveMembers)
        {
            if (string.IsNullOrWhiteSpace(member.MemberId) ||
                string.IsNullOrWhiteSpace(member.Incarnation) ||
                !seenMembers.Add(member.MemberId))
            {
                return null;
            }

            var supports = member.Capabilities.Count(candidate =>
                candidate.Capability == capability &&
                candidate.ReaderContractVersion >= gate.MinimumReaderContractVersion &&
                string.Equals(
                    candidate.ContractId,
                    gate.RequiredContractId,
                    StringComparison.Ordinal));
            if (supports != 1)
                return null;
            confirmedMembers++;
        }

        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = capability,
            Status = gate.Status,
            AuthorityActorId = document.AuthorityActorId,
            AuthorityStateVersion = document.StateVersion,
            CapabilityEpoch = gate.CapabilityEpoch,
            MembershipEpoch = membership.MembershipEpoch,
            DeploymentRevision = membership.DeploymentRevision,
            MinimumReaderContractVersion = gate.MinimumReaderContractVersion,
            MembershipObservedAt = membership.ObservedAt.Clone(),
            MembershipValidUntil = membership.ValidUntil.Clone(),
            ActiveMemberCount = membership.ActiveMembers.Count,
            ConfirmedMemberCount = confirmedMembers,
            MembershipDigest = membership.MembershipDigest,
            ContractId = gate.RequiredContractId,
        };
        admission.AdmittedMembers.Add(membership.ActiveMembers.Select(member =>
            new RuntimeFleetAdmittedMember
            {
                MemberId = member.MemberId,
                Incarnation = member.Incarnation,
            }));
        return admission;
    }

    public async Task<RuntimeFleetCapabilityQuiescenceEvidence?> GetQuiescenceAsync(
        RuntimeFleetCapability capability,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_documentReader == null)
            return null;

        var document = await _documentReader.GetAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            ct);
        if (document == null ||
            !string.Equals(
                document.AuthorityActorId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal) ||
            document.StateVersion <= 0)
        {
            return null;
        }

        var matchingGates = document.Gates
            .Where(gate => gate.Capability == capability)
            .ToArray();
        if (matchingGates.Length != 1)
            return null;

        var gate = matchingGates[0];
        if (gate.Status != RuntimeFleetCapabilityGateStatus.Quiesced ||
            gate.CapabilityEpoch != long.MaxValue ||
            gate.QuiescenceReaderContractVersion <= 0 ||
            gate.MembershipEpoch <= 0 ||
            string.IsNullOrWhiteSpace(gate.RequiredContractId) ||
            string.IsNullOrWhiteSpace(gate.MembershipDigest) ||
            string.IsNullOrWhiteSpace(gate.DeploymentRevision) ||
            gate.ChangedAt == null ||
            string.IsNullOrWhiteSpace(gate.LastTransitionId))
        {
            return null;
        }

        return new RuntimeFleetCapabilityQuiescenceEvidence
        {
            Capability = capability,
            AuthorityActorId = document.AuthorityActorId,
            AuthorityStateVersion = document.StateVersion,
            CapabilityEpoch = gate.CapabilityEpoch,
            ContractId = gate.RequiredContractId,
            QuiescenceReaderContractVersion = gate.QuiescenceReaderContractVersion,
            QuiescedMembershipEpoch = gate.MembershipEpoch,
            QuiescedMembershipDigest = gate.MembershipDigest,
            QuiescedDeploymentRevision = gate.DeploymentRevision,
            QuiescedAt = gate.ChangedAt.Clone(),
            QuiescenceTransitionId = gate.LastTransitionId,
        };
    }
}
