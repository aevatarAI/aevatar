using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Core.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RuntimeFleetCapabilityAdmissionValidationTests
{
    private const string ContractId = "test.contract.v1";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 18, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IsGrantedAsync_WhenProofAndLocalMembershipAreExact_ShouldGrant()
    {
        var granted = await EvaluateAsync(CreateAdmission(), CurrentMembership());

        granted.Should().BeTrue();
    }

    [Theory]
    [InlineData(InvalidAdmissionCase.Revoked)]
    [InlineData(InvalidAdmissionCase.Stale)]
    [InlineData(InvalidAdmissionCase.WrongAuthority)]
    [InlineData(InvalidAdmissionCase.MissingAuthorityVersion)]
    [InlineData(InvalidAdmissionCase.MissingCapabilityEpoch)]
    [InlineData(InvalidAdmissionCase.WrongMembershipEpoch)]
    [InlineData(InvalidAdmissionCase.WrongMembershipDigest)]
    [InlineData(InvalidAdmissionCase.WrongDeploymentRevision)]
    [InlineData(InvalidAdmissionCase.WrongContract)]
    [InlineData(InvalidAdmissionCase.ReaderVersionTooOld)]
    public async Task IsGrantedAsync_WhenProofDoesNotMatchLiveRequirement_ShouldDeny(
        InvalidAdmissionCase invalidCase)
    {
        var admission = CreateAdmission();
        ApplyInvalidCase(admission, invalidCase);

        var granted = await EvaluateAsync(admission, CurrentMembership());

        granted.Should().BeFalse();
    }

    [Fact]
    public async Task IsGrantedAsync_WhenLocalIncarnationIsNotAdmitted_ShouldDeny()
    {
        var membership = new RuntimeLocalMembershipIdentity(
            7,
            "digest-a",
            "revision-a",
            "member-a",
            "inc-restarted");

        var granted = await EvaluateAsync(CreateAdmission(), membership);

        granted.Should().BeFalse();
    }

    [Fact]
    public async Task IsGrantedAsync_WhenProjectedProofDescribesPreviousMembership_ShouldDenyImmediately()
    {
        var changedMembership = new RuntimeLocalMembershipIdentity(
            8,
            "digest-b",
            "revision-b",
            "member-a",
            "inc-a");

        var granted = await EvaluateAsync(CreateAdmission(), changedMembership);

        granted.Should().BeFalse();
    }

    [Fact]
    public async Task IsGrantedAsync_WhenSameMembershipOpenProjectionIsDelayed_ShouldDenyAtValidityDeadline()
    {
        var admission = CreateAdmission();
        var validUntil = admission.MembershipValidUntil.ToDateTimeOffset();

        var beforeDeadline = await EvaluateAsync(
            admission,
            CurrentMembership(),
            validUntil.AddTicks(-1));
        var atDeadline = await EvaluateAsync(
            admission,
            CurrentMembership(),
            validUntil);

        beforeDeadline.Should().BeTrue();
        atDeadline.Should().BeFalse();
    }

    private static Task<bool> EvaluateAsync(
        RuntimeFleetCapabilityAdmission admission,
        RuntimeLocalMembershipIdentity membership,
        DateTimeOffset? now = null) =>
        RuntimeFleetCapabilityAdmissionValidation.IsGrantedAsync(
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            ContractId,
            1,
            new StubAdmissionReader(admission),
            new StubMembershipReader(membership),
            new FixedTimeProvider(now ?? Now));

    private static RuntimeFleetCapabilityAdmission CreateAdmission()
    {
        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            Status = RuntimeFleetCapabilityGateStatus.Open,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 9,
            CapabilityEpoch = 3,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            MinimumReaderContractVersion = 1,
            MembershipObservedAt = Timestamp.FromDateTimeOffset(Now.AddSeconds(-5)),
            MembershipValidUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(1)),
            ActiveMemberCount = 2,
            ConfirmedMemberCount = 2,
            MembershipDigest = "digest-a",
            ContractId = ContractId,
        };
        admission.AdmittedMembers.Add(
            new RuntimeFleetAdmittedMember { MemberId = "member-a", Incarnation = "inc-a" });
        admission.AdmittedMembers.Add(
            new RuntimeFleetAdmittedMember { MemberId = "member-b", Incarnation = "inc-b" });
        return admission;
    }

    private static RuntimeLocalMembershipIdentity CurrentMembership() =>
        new(7, "digest-a", "revision-a", "member-a", "inc-a");

    private static void ApplyInvalidCase(
        RuntimeFleetCapabilityAdmission admission,
        InvalidAdmissionCase invalidCase)
    {
        switch (invalidCase)
        {
            case InvalidAdmissionCase.Revoked:
                admission.Status = RuntimeFleetCapabilityGateStatus.Revoked;
                break;
            case InvalidAdmissionCase.Stale:
                admission.MembershipValidUntil = Timestamp.FromDateTimeOffset(Now);
                break;
            case InvalidAdmissionCase.WrongAuthority:
                admission.AuthorityActorId = "business-actor";
                break;
            case InvalidAdmissionCase.MissingAuthorityVersion:
                admission.AuthorityStateVersion = 0;
                break;
            case InvalidAdmissionCase.MissingCapabilityEpoch:
                admission.CapabilityEpoch = 0;
                break;
            case InvalidAdmissionCase.WrongMembershipEpoch:
                admission.MembershipEpoch++;
                break;
            case InvalidAdmissionCase.WrongMembershipDigest:
                admission.MembershipDigest = "digest-b";
                break;
            case InvalidAdmissionCase.WrongDeploymentRevision:
                admission.DeploymentRevision = "revision-b";
                break;
            case InvalidAdmissionCase.WrongContract:
                admission.ContractId = "test.contract.v2";
                break;
            case InvalidAdmissionCase.ReaderVersionTooOld:
                admission.MinimumReaderContractVersion = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null);
        }
    }

    private sealed class StubAdmissionReader(RuntimeFleetCapabilityAdmission admission)
        : IRuntimeFleetCapabilityAdmissionReader
    {
        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<RuntimeFleetCapabilityAdmission?>(admission.Clone());
        }
    }

    private sealed class StubMembershipReader(RuntimeLocalMembershipIdentity membership)
        : IRuntimeLocalMembershipIdentityReader
    {
        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<RuntimeLocalMembershipIdentity?>(membership);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public enum InvalidAdmissionCase
    {
        Revoked,
        Stale,
        WrongAuthority,
        MissingAuthorityVersion,
        MissingCapabilityEpoch,
        WrongMembershipEpoch,
        WrongMembershipDigest,
        WrongDeploymentRevision,
        WrongContract,
        ReaderVersionTooOld,
    }
}
