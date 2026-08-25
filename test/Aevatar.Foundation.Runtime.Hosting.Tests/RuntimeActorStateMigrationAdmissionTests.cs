using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using FluentAssertions;
using Google.Protobuf;
using Orleans.Runtime;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeActorStateMigrationAdmissionTests
{
    [Fact]
    public async Task ApplyAndPersistAsync_ShouldApplyConsecutiveChainAndWriteSnapshotAndIdentityOnce()
    {
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = new EventEnvelope { Id = "before" }.ToByteArray(),
        });
        var proxy = Proxy(persistentState);
        var implementation = Implementation(
            supportedVersion: 2,
            Step(0, 1, state => state.Id += ":v1"),
            Step(1, 2, state => state.Id += ":v2"));

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            implementation,
            new StubAdmissionReader(CreateAdmission()),
            CurrentMembership());

        migrated.Should().BeTrue();
        proxy.WriteCount.Should().Be(1);
        proxy.State.Identity!.StateSchemaVersion.Should().Be(2);
        proxy.State.Identity.StateSchemaAdoptions.Should().HaveCount(2);
        proxy.State.Identity.StateSchemaAdoptions.Should().OnlyContain(receipt =>
            receipt.EvidenceStatus == RuntimeFleetCapabilityGateStatus.Open &&
            string.IsNullOrEmpty(receipt.QuiescenceTransitionId));
        proxy.State.AgentStateTypeName.Should().Be(typeof(EventEnvelope).FullName);
        EventEnvelope.Parser.ParseFrom(proxy.State.AgentStateSnapshot).Id.Should().Be("before:v1:v2");
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenAlreadyCurrent_ShouldNotWrite()
    {
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 1 },
        });
        persistentState.State.Identity!.StateSchemaAdoptions.Add(CreateReceipt(1));
        persistentState.State.Identity.StateSchemaAdoptions[0].EvidenceStatus.Should().Be(
            RuntimeFleetCapabilityGateStatus.Unspecified,
            "legacy OPEN receipts predate the evidence_status field");
        var proxy = Proxy(persistentState);

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        migrated.Should().BeFalse();
        proxy.WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenPersistedVersionIsNegative_ShouldRejectWithoutMutation()
    {
        var snapshot = new EventEnvelope { Id = "unchanged" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = -1 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });
        var proxy = Proxy(persistentState);

        var act = () => RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*negative persisted state schema version*");
        AssertUnchanged(proxy, -1, snapshot);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenPersistedVersionIsNewer_ShouldRejectOldReader()
    {
        var snapshot = new EventEnvelope { Id = "v2" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 2 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });
        var proxy = Proxy(persistentState);

        var act = () => RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*newer than supported version 1*");
        AssertUnchanged(proxy, 2, snapshot);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenSnapshotTypeDoesNotMatch_ShouldRejectEvenIfSnapshotIsEmpty()
    {
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(RuntimeActorIdentity).FullName,
            AgentStateSnapshot = [],
        });
        var proxy = Proxy(persistentState);

        var act = () => RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match state contract*");
        proxy.WriteCount.Should().Be(0);
        proxy.State.Identity!.StateSchemaVersion.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenChainIsIncomplete_ShouldRejectWithoutMutation()
    {
        var snapshot = new EventEnvelope { Id = "v0" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });
        var proxy = Proxy(persistentState);

        var act = () => RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(2, Step(1, 2, _ => { })),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no complete typed state migration chain from version 0 to 1*");
        AssertUnchanged(proxy, 0, snapshot);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenWriteFails_ShouldRestoreInMemorySnapshotAndIdentity()
    {
        var snapshot = new EventEnvelope { Id = "v0" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });
        var proxy = Proxy(persistentState);
        proxy.WriteException = new InvalidOperationException("write failed");

        var act = () => RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, state => state.Id = "v1")),
            new StubAdmissionReader(CreateAdmission()),
            CurrentMembership());

        // The storage failure surfaces as the typed persistence exception (with the store's
        // exception inner). The in-memory row is put back to the pre-write shape so the
        // observing activation holds no half-migrated bytes, but that is not evidence about the
        // durable row: the grain discards the activation and re-reads durable state (see
        // RuntimeActorGrainMigrationFailClosedIntegrationTests).
        var thrown = await act.Should().ThrowAsync<RuntimeActorStateMigrationPersistenceException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("write failed");
        thrown.Which.AgentKind.Should().Be("test.migrated");
        thrown.Which.PersistedStateSchemaVersion.Should().Be(0);
        thrown.Which.TargetStateSchemaVersion.Should().Be(1);
        proxy.WriteCount.Should().Be(1);
        proxy.State.AgentStateTypeName.Should().Be(typeof(EventEnvelope).FullName);
        proxy.State.Identity!.StateSchemaVersion.Should().Be(0);
        proxy.State.Identity.StateSchemaAdoptions.Should().BeEmpty();
        proxy.State.AgentStateSnapshot.Should().Equal(snapshot);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenGateProofIsAbsent_ShouldLeaveLegacyStateUnchanged()
    {
        var snapshot = new EventEnvelope { Id = "legacy" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });
        var proxy = Proxy(persistentState);

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, state => state.Id = "must-not-run")),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        migrated.Should().BeFalse();
        AssertUnchanged(proxy, 0, snapshot);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenQuiescenceReaderIsAbsent_ShouldBlockQuiescedMigration()
    {
        var snapshot = new EventEnvelope { Id = "legacy" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(
                1,
                Step(
                    0,
                    1,
                    state => state.Id = "must-not-run",
                    RuntimeFleetCapabilityGateStatus.Quiesced)),
            new StubAdmissionReader(CreateAdmission()),
            CurrentMembership());

        migrated.Should().BeFalse();
        AssertUnchanged(Proxy(persistentState), 0, snapshot);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenExactQuiescenceExists_ShouldPersistTerminalEvidenceReceipt()
    {
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = new EventEnvelope { Id = "legacy" }.ToByteArray(),
        });
        var evidence = CreateQuiescenceEvidence();

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(
                1,
                Step(
                    0,
                    1,
                    state => state.Id = "quiesced",
                    RuntimeFleetCapabilityGateStatus.Quiesced)),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader(),
            quiescenceReader: new StubQuiescenceReader(evidence));

        migrated.Should().BeTrue();
        var receipt = Proxy(persistentState).State.Identity!.StateSchemaAdoptions
            .Should().ContainSingle().Subject;
        receipt.EvidenceStatus.Should().Be(RuntimeFleetCapabilityGateStatus.Quiesced);
        receipt.QuiescenceTransitionId.Should().Be(evidence.QuiescenceTransitionId);
        receipt.CapabilityEpoch.Should().Be(long.MaxValue);
        receipt.AuthorityStateVersion.Should().Be(evidence.AuthorityStateVersion);
        receipt.MembershipEpoch.Should().Be(evidence.QuiescedMembershipEpoch);
        receipt.MembershipDigest.Should().Be(evidence.QuiescedMembershipDigest);
        receipt.DeploymentRevision.Should().Be(evidence.QuiescedDeploymentRevision);
        EventEnvelope.Parser.ParseFrom(Proxy(persistentState).State.AgentStateSnapshot)
            .Id.Should().Be("quiesced");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("contract")]
    [InlineData("version")]
    [InlineData("epoch")]
    public async Task ApplyAndPersistAsync_WhenPersistedQuiescenceReceiptIsNotExact_ShouldReject(
        string invalidField)
    {
        var identity = new RuntimeActorIdentity
        {
            Kind = "test.migrated",
            StateSchemaVersion = 1,
        };
        var receipt = CreateQuiescenceAdoptionReceipt();
        switch (invalidField)
        {
            case "status":
                receipt.EvidenceStatus = RuntimeFleetCapabilityGateStatus.Open;
                break;
            case "contract":
                receipt.RequiredContractId = "wrong.contract";
                break;
            case "version":
                receipt.RequiredContractVersion = 2;
                break;
            case "epoch":
                receipt.CapabilityEpoch = 4;
                break;
        }
        identity.StateSchemaAdoptions.Add(receipt);
        var persistentState = CreatePersistentState(new RuntimeActorGrainState { Identity = identity });

        var act = () => RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(
                1,
                Step(0, 1, _ => { }, RuntimeFleetCapabilityGateStatus.Quiesced)),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader(),
            quiescenceReader: new StubQuiescenceReader(CreateQuiescenceEvidence()));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no unique matching adoption receipt*");
        Proxy(persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenProofMembershipDoesNotMatchLocalEpoch_ShouldNotCutOver()
    {
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
        });
        var admission = CreateAdmission();
        admission.MembershipEpoch = 8;

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new StubAdmissionReader(admission),
            CurrentMembership());

        migrated.Should().BeFalse();
        persistentState.State.Identity!.StateSchemaVersion.Should().Be(0);
        Proxy(persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenProofDigestDoesNotMatchLocalMembership_ShouldNotCutOver()
    {
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
        });
        var membership = new StubMembershipReader(new RuntimeLocalMembershipIdentity(
            7,
            "different-digest",
            "revision-a",
            "member-a",
            "inc-a"));

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new StubAdmissionReader(CreateAdmission()),
            membership);

        migrated.Should().BeFalse();
        persistentState.State.Identity!.StateSchemaVersion.Should().Be(0);
        Proxy(persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenLocalMemberIsNotInAdmittedSet_ShouldNotCutOver()
    {
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
        });
        var membership = new StubMembershipReader(new RuntimeLocalMembershipIdentity(
            7,
            "digest-a",
            "revision-a",
            "member-missing",
            "inc-missing"));

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new StubAdmissionReader(CreateAdmission()),
            membership);

        migrated.Should().BeFalse();
        persistentState.State.Identity!.StateSchemaVersion.Should().Be(0);
        Proxy(persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenSchemaWasAlreadyAdopted_ShouldIgnoreLiveGateRevocation()
    {
        var identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 1 };
        identity.StateSchemaAdoptions.Add(CreateReceipt(1));
        var persistentState = CreatePersistentState(new RuntimeActorGrainState { Identity = identity });

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        migrated.Should().BeFalse();
        Proxy(persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenCurrentSchemaHasNoReceipt_ShouldFailClosed()
    {
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 1 },
        });

        var act = () => RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, _ => { })),
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no unique matching adoption receipt*");
        Proxy(persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenEarlierReceiptIsCorrupt_ShouldNotAdvanceAnotherVersion()
    {
        var identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 1 };
        var receipt = CreateReceipt(1);
        receipt.RequiredContractId = "wrong.contract";
        identity.StateSchemaAdoptions.Add(receipt);
        var persistentState = CreatePersistentState(new RuntimeActorGrainState { Identity = identity });

        var act = () => RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(2, Step(0, 1, _ => { }), Step(1, 2, _ => { })),
            new StubAdmissionReader(CreateAdmission()),
            CurrentMembership());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*schema version 1 has no unique matching adoption receipt*");
        persistentState.State.Identity!.StateSchemaVersion.Should().Be(1);
        Proxy(persistentState).WriteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenMembershipChangesBeforeWrite_ShouldKeepLegacyState()
    {
        var snapshot = new EventEnvelope { Id = "legacy" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });
        var membership = new SequenceMembershipReader(
            new RuntimeLocalMembershipIdentity(7, "digest-a", "revision-a", "member-a", "inc-a"),
            new RuntimeLocalMembershipIdentity(8, "digest-b", "revision-b", "member-a", "inc-b"));

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, state => state.Id = "migrated")),
            new StubAdmissionReader(CreateAdmission()),
            membership);

        migrated.Should().BeFalse();
        AssertUnchanged(Proxy(persistentState), 0, snapshot);
        membership.ReadCount.Should().Be(2);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenMembershipReaderFails_ShouldKeepLegacyState()
    {
        var snapshot = new EventEnvelope { Id = "legacy" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, state => state.Id = "must-not-run")),
            new StubAdmissionReader(CreateAdmission()),
            new ThrowingMembershipReader());

        migrated.Should().BeFalse();
        AssertUnchanged(Proxy(persistentState), 0, snapshot);
    }

    [Fact]
    public async Task ApplyAndPersistAsync_WhenAdmissionReaderFails_ShouldKeepLegacyState()
    {
        var snapshot = new EventEnvelope { Id = "legacy" }.ToByteArray();
        var persistentState = CreatePersistentState(new RuntimeActorGrainState
        {
            Identity = new RuntimeActorIdentity { Kind = "test.migrated", StateSchemaVersion = 0 },
            AgentStateTypeName = typeof(EventEnvelope).FullName,
            AgentStateSnapshot = snapshot,
        });

        var migrated = await RuntimeActorStateMigrationPersistence.ApplyAndPersistAsync(
            persistentState,
            Implementation(1, Step(0, 1, state => state.Id = "must-not-run")),
            new ThrowingAdmissionReader(),
            CurrentMembership());

        migrated.Should().BeFalse();
        AssertUnchanged(Proxy(persistentState), 0, snapshot);
    }

    private static AgentImplementation Implementation(
        int supportedVersion,
        params ActorStateMigrationStep[] steps) =>
        new(
            _ => null!,
            typeof(EventEnvelope),
            new AgentImplementationMetadata(
                "test.migrated",
                "TestMigratedAgent",
                supportedVersion),
            steps);

    private static ActorStateMigrationStep Step(
        int fromVersion,
        int toVersion,
        Action<EventEnvelope> mutate,
        RuntimeFleetCapabilityGateStatus requiredGateStatus =
            RuntimeFleetCapabilityGateStatus.Open) =>
        new(
            fromVersion,
            toVersion,
            typeof(EventEnvelope),
            typeof(RuntimeActorStateMigrationAdmissionTests),
            bytes =>
            {
                var state = EventEnvelope.Parser.ParseFrom(bytes);
                mutate(state);
                return state.ToByteArray();
            },
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            "test.contract.v1",
            1,
            requiredGateStatus);

    private static RuntimeFleetCapabilityAdmission CreateAdmission()
    {
        var now = DateTimeOffset.UtcNow;
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
            MembershipObservedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddSeconds(-5)),
            MembershipValidUntil = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(now.AddMinutes(1)),
            ActiveMemberCount = 2,
            ConfirmedMemberCount = 2,
            MembershipDigest = "digest-a",
            ContractId = "test.contract.v1",
        };
        admission.AdmittedMembers.Add(
            new RuntimeFleetAdmittedMember { MemberId = "member-a", Incarnation = "inc-a" });
        admission.AdmittedMembers.Add(
            new RuntimeFleetAdmittedMember { MemberId = "member-b", Incarnation = "inc-b" });
        return admission;
    }

    private static RuntimeActorStateSchemaAdoptionReceipt CreateReceipt(int schemaVersion) =>
        new()
        {
            StateSchemaVersion = schemaVersion,
            RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RequiredContractId = "test.contract.v1",
            RequiredContractVersion = 1,
            CapabilityEpoch = 3,
            AuthorityStateVersion = 9,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            AdoptedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = "digest-a",
        };

    private static RuntimeActorStateSchemaAdoptionReceipt CreateQuiescenceAdoptionReceipt() =>
        new()
        {
            StateSchemaVersion = 1,
            RequiredCapability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RequiredContractId = "test.contract.v1",
            RequiredContractVersion = 1,
            CapabilityEpoch = long.MaxValue,
            AuthorityStateVersion = 17,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-quiesced",
            AdoptedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = "digest-quiesced",
            EvidenceStatus = RuntimeFleetCapabilityGateStatus.Quiesced,
            QuiescenceTransitionId = "transition-quiesced",
        };

    private static RuntimeFleetCapabilityQuiescenceEvidence CreateQuiescenceEvidence() =>
        new()
        {
            Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 17,
            CapabilityEpoch = long.MaxValue,
            ContractId = "test.contract.v1",
            QuiescenceReaderContractVersion = 1,
            QuiescedMembershipEpoch = 7,
            QuiescedMembershipDigest = "digest-quiesced",
            QuiescedDeploymentRevision = "revision-quiesced",
            QuiescedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                DateTimeOffset.UtcNow),
            QuiescenceTransitionId = "transition-quiesced",
        };

    private static IRuntimeLocalMembershipIdentityReader CurrentMembership() =>
        new StubMembershipReader(new RuntimeLocalMembershipIdentity(
            7,
            "digest-a",
            "revision-a",
            "member-a",
            "inc-a"));

    private sealed class StubAdmissionReader(RuntimeFleetCapabilityAdmission? admission)
        : IRuntimeFleetCapabilityAdmissionReader
    {
        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(admission?.Clone());
        }
    }

    private sealed class StubMembershipReader(RuntimeLocalMembershipIdentity? membership)
        : IRuntimeLocalMembershipIdentityReader
    {
        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(membership);
        }
    }

    private sealed class StubQuiescenceReader(
        RuntimeFleetCapabilityQuiescenceEvidence? evidence)
        : IRuntimeFleetCapabilityQuiescenceReader
    {
        public Task<RuntimeFleetCapabilityQuiescenceEvidence?> GetQuiescenceAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(evidence?.Clone());
        }
    }

    private sealed class SequenceMembershipReader(
        params RuntimeLocalMembershipIdentity?[] memberships)
        : IRuntimeLocalMembershipIdentityReader
    {
        private int _readCount;

        internal int ReadCount => _readCount;

        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _readCount) - 1;
            return ValueTask.FromResult(
                memberships[Math.Min(index, memberships.Length - 1)]);
        }
    }

    private sealed class ThrowingMembershipReader : IRuntimeLocalMembershipIdentityReader
    {
        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(
            CancellationToken ct = default) =>
            ValueTask.FromException<RuntimeLocalMembershipIdentity?>(
                new IOException("membership unavailable"));
    }

    private sealed class ThrowingAdmissionReader : IRuntimeFleetCapabilityAdmissionReader
    {
        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default) =>
            Task.FromException<RuntimeFleetCapabilityAdmission?>(
                new IOException("authority read model unavailable"));
    }

    private static IPersistentState<RuntimeActorGrainState> CreatePersistentState(
        RuntimeActorGrainState state)
    {
        var persistentState = DispatchProxy.Create<
            IPersistentState<RuntimeActorGrainState>,
            PersistentStateProxy>();
        Proxy(persistentState).State = state;
        return persistentState;
    }

    private static PersistentStateProxy Proxy(IPersistentState<RuntimeActorGrainState> state) =>
        (PersistentStateProxy)(object)state;

    private static void AssertUnchanged(
        PersistentStateProxy proxy,
        int schemaVersion,
        byte[] snapshot)
    {
        proxy.WriteCount.Should().Be(0);
        proxy.State.Identity!.StateSchemaVersion.Should().Be(schemaVersion);
        proxy.State.AgentStateSnapshot.Should().Equal(snapshot);
    }

    private class PersistentStateProxy : DispatchProxy
    {
        public RuntimeActorGrainState State { get; set; } = new();

        public int WriteCount { get; private set; }

        public Exception? WriteException { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_State":
                    return State;
                case "set_State":
                    State = (RuntimeActorGrainState)args![0]!;
                    return null;
                case "WriteStateAsync":
                    WriteCount++;
                    return WriteException == null
                        ? Task.CompletedTask
                        : Task.FromException(WriteException);
                case "ReadStateAsync":
                case "ClearStateAsync":
                    return Task.CompletedTask;
                case "get_RecordExists":
                    return true;
                case "get_Etag":
                    return string.Empty;
                case "set_Etag":
                    return null;
                default:
                    return targetMethod?.ReturnType is { IsValueType: true } returnType
                        ? Activator.CreateInstance(returnType)
                        : null;
            }
        }
    }
}
