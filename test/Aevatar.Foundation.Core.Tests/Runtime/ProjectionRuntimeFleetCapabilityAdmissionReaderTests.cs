using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Projection.Runtime;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests.Runtime;

public sealed class ProjectionRuntimeFleetCapabilityAdmissionReaderTests
{
    [Fact]
    public async Task AddRuntimeFleetCapabilityProjection_WithoutDocumentReader_ShouldReturnNoAdmission()
    {
        var services = new ServiceCollection();
        services.AddRuntimeFleetCapabilityProjection();
        using var provider = services.BuildServiceProvider();

        var reader = provider.GetRequiredService<IRuntimeFleetCapabilityAdmissionReader>();
        var quiescenceReader = provider.GetRequiredService<IRuntimeFleetCapabilityQuiescenceReader>();

        reader.Should().BeOfType<ProjectionRuntimeFleetCapabilityAdmissionReader>();
        quiescenceReader.Should().BeSameAs(reader);
        (await reader.GetAsync(RuntimeFleetCapability.WorkflowNormalizedStateWritesV1))
            .Should().BeNull();
        (await quiescenceReader.GetQuiescenceAsync(
                RuntimeFleetCapability.WorkflowNormalizedStateWritesV1))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WithMultipleDocumentReaders_ShouldFailClosedWithoutChoosingOne()
    {
        var first = new RecordingDocumentReader();
        var second = new RecordingDocumentReader();
        var reader = new ProjectionRuntimeFleetCapabilityAdmissionReader(
            new IProjectionDocumentReader<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>[]
            {
                first,
                second,
            });

        var admission = await reader.GetAsync(
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1);

        admission.Should().BeNull();
        first.ReadCount.Should().Be(0);
        second.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_WithSingleDocumentReader_ShouldReadAuthorityDocument()
    {
        var documentReader = new RecordingDocumentReader();
        var reader = new ProjectionRuntimeFleetCapabilityAdmissionReader([documentReader]);

        var admission = await reader.GetAsync(
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1);

        admission.Should().BeNull();
        documentReader.ReadCount.Should().Be(1);
        documentReader.LastKey.Should().Be(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
    }

    [Fact]
    public async Task GetAsync_WithCompatibilityTombstone_ShouldReturnNoAdmission()
    {
        var documentReader = new RecordingDocumentReader(
            CreateDocument(RuntimeFleetCapabilityGateStatus.Revoked));
        var reader = new ProjectionRuntimeFleetCapabilityAdmissionReader([documentReader]);

        var admission = await reader.GetAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        var evidence = await reader.GetQuiescenceAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);

        admission.Should().BeNull("the pre-bridge reader accepts only OPEN gates");
        evidence.Should().BeNull(
            "REVOKED/max is only the compatibility tombstone, not the typed quiescence marker");
    }

    [Fact]
    public async Task GetQuiescenceAsync_WithTypedQuiescedGate_ShouldReturnTerminalEvidenceButNoAdmission()
    {
        var documentReader = new RecordingDocumentReader(
            CreateDocument(RuntimeFleetCapabilityGateStatus.Quiesced));
        var reader = new ProjectionRuntimeFleetCapabilityAdmissionReader([documentReader]);

        var admission = await reader.GetAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        var evidence = await reader.GetQuiescenceAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);

        admission.Should().BeNull("QUIESCED is terminal evidence, never live OPEN admission");
        evidence.Should().NotBeNull();
        evidence!.CapabilityEpoch.Should().Be(long.MaxValue);
        evidence.QuiescenceReaderContractVersion.Should().Be(3);
        evidence.ContractId.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1);
        evidence.QuiescedMembershipEpoch.Should().Be(7);
        evidence.QuiescedDeploymentRevision.Should().Be("bridge-revision");
        evidence.QuiescenceTransitionId.Should().Be("transition:quiesce");
    }

    [Fact]
    public async Task GetQuiescenceAsync_AfterCurrentMembershipChanges_ShouldKeepHistoricalTerminalEvidence()
    {
        var document = CreateDocument(RuntimeFleetCapabilityGateStatus.Quiesced);
        document.StateVersion = 19;
        document.Membership.MembershipEpoch = 8;
        document.Membership.DeploymentRevision = "phase-b-revision";
        document.Membership.ActiveMembers[0].Incarnation = "inc-b";
        document.Membership.ActiveMembers[0].DeploymentRevision = "phase-b-revision";
        document.Membership.ActiveMembers[0].Capabilities.Clear();
        document.Membership.MembershipDigest = RuntimeFleetMembershipDigest.Compute(document.Membership);
        var reader = new ProjectionRuntimeFleetCapabilityAdmissionReader(
            [new RecordingDocumentReader(document)]);

        var evidence = await reader.GetQuiescenceAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);

        evidence.Should().NotBeNull();
        evidence!.AuthorityStateVersion.Should().Be(19);
        evidence.QuiescedMembershipEpoch.Should().Be(7);
        evidence.QuiescedDeploymentRevision.Should().Be("bridge-revision");
        evidence.QuiescenceTransitionId.Should().Be("transition:quiesce");
    }

    private static RuntimeFleetCapabilityAuthorityCurrentStateDocument CreateDocument(
        RuntimeFleetCapabilityGateStatus status)
    {
        var observedAt = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        var membership = new RuntimeFleetMembershipSnapshot
        {
            MembershipEpoch = 7,
            DeploymentRevision = "bridge-revision",
            ObservedAt = Timestamp.FromDateTimeOffset(observedAt),
            ValidUntil = Timestamp.FromDateTimeOffset(observedAt.AddMinutes(1)),
        };
        var member = new RuntimeFleetMember
        {
            MemberId = "silo-a",
            Incarnation = "inc-a",
            DeploymentRevision = membership.DeploymentRevision,
        };
        member.Capabilities.Add(new RuntimeFleetMemberCapability
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            ReaderContractVersion = 3,
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
        });
        membership.ActiveMembers.Add(member);
        membership.MembershipDigest = RuntimeFleetMembershipDigest.Compute(membership);

        var document = new RuntimeFleetCapabilityAuthorityCurrentStateDocument
        {
            Id = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            StateVersion = 11,
            Membership = membership,
        };
        document.Gates.Add(new RuntimeFleetCapabilityGateState
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            Status = status,
            CapabilityEpoch = long.MaxValue,
            MembershipEpoch = membership.MembershipEpoch,
            MembershipDigest = membership.MembershipDigest,
            DeploymentRevision = membership.DeploymentRevision,
            MinimumReaderContractVersion = 3,
            RequiredContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
            QuiescenceReaderContractVersion = 3,
            ChangedAt = Timestamp.FromDateTimeOffset(observedAt),
            LastTransitionId = "transition:quiesce",
        });
        return document;
    }

    private sealed class RecordingDocumentReader(
        RuntimeFleetCapabilityAuthorityCurrentStateDocument? document = null)
        : IProjectionDocumentReader<RuntimeFleetCapabilityAuthorityCurrentStateDocument, string>
    {
        public int ReadCount { get; private set; }

        public string? LastKey { get; private set; }

        public Task<RuntimeFleetCapabilityAuthorityCurrentStateDocument?> GetAsync(
            string key,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReadCount++;
            LastKey = key;
            return Task.FromResult(document?.Clone());
        }

        public Task<ProjectionDocumentQueryResult<RuntimeFleetCapabilityAuthorityCurrentStateDocument>>
            QueryAsync(
                ProjectionDocumentQuery query,
                CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
