using System.Reflection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Workflow.Projection.DependencyInjection;
using Aevatar.Workflow.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowExecutionMaterializationScopeRegistrationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 18, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AddWorkflowExecutionProjectionCQRS_ShouldRegisterStableVersionedScopeAndManifestAdvertisement()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentKindRegistry>();

        registry.TryGetKindForAgentType(
                typeof(WorkflowExecutionMaterializationScopeGAgent),
                out var kind)
            .Should()
            .BeTrue();
        kind.Should().Be(WorkflowExecutionMaterializationScopeGAgent.AgentKind);
        kind.Should().Be(
            "projection.materialization-scope.workflow-execution-materialization-context");

        var implementation = registry.Resolve(kind);
        implementation.Metadata.StateSchemaVersion.Should().Be(
            WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion);
        implementation.StateContractType.Should().Be(typeof(ProjectionScopeState));
        var migrations = implementation.StateMigrations.Should().HaveCount(2).And.Subject;
        var graphMigration = migrations.Single(static migration => migration.FromStateVersion == 0);
        graphMigration.MigrationType.Should()
            .Be(typeof(WorkflowExecutionMaterializationScopeStateV0ToV1Migration));
        graphMigration.ToStateVersion.Should()
            .Be(WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion);
        graphMigration.RequiredCapability.Should()
            .Be(RuntimeFleetCapability.ProjectionIncrementalGraphV1);
        graphMigration.RequiredContractId.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1);
        graphMigration.RequiredContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion);
        graphMigration.RequiredGateStatus.Should().Be(RuntimeFleetCapabilityGateStatus.Open);

        var activationSealMigration = migrations.Single(static migration => migration.FromStateVersion == 1);
        activationSealMigration.MigrationType.Should()
            .Be(typeof(WorkflowExecutionMaterializationScopeStateV1ToV2Migration));
        activationSealMigration.ToStateVersion.Should()
            .Be(WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion);
        activationSealMigration.RequiredCapability.Should()
            .Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
        activationSealMigration.RequiredContractId.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1);
        activationSealMigration.RequiredContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion);
        activationSealMigration.RequiredGateStatus.Should().Be(RuntimeFleetCapabilityGateStatus.Open);

        var advertisement = provider
            .GetServices<IRuntimeFleetCapabilityAdvertisement>()
            .Single(candidate =>
                candidate.GetCapability().Capability ==
                RuntimeFleetCapability.ProjectionIncrementalGraphV1);
        var capability = advertisement.GetCapability();
        capability.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1);
        capability.ReaderContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion);
        advertisement.GetReaderImplementationType().Should()
            .Be(typeof(WorkflowExecutionMaterializationScopeGAgent));
    }

    [Fact]
    public void WorkflowScopeDurableRecovery_ShouldRequireExactSchemaAdoptionReceipt()
    {
        var exactReceipt = CreateGraphAdoptionReceipt();
        var activationSealReceipt = CreateActivationSealAdoptionReceipt();

        IsDurableRecoveryEnabled(null).Should().BeFalse();
        IsDurableRecoveryEnabled(new RuntimeActorStateSchemaContext(
                WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                0,
                [exactReceipt]))
            .Should()
            .BeFalse();
        IsDurableRecoveryEnabled(new RuntimeActorStateSchemaContext(
                WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion,
                []))
            .Should()
            .BeFalse();

        var wrongReceipt = exactReceipt.Clone();
        wrongReceipt.RequiredContractVersion++;
        IsDurableRecoveryEnabled(new RuntimeActorStateSchemaContext(
                WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion,
                [wrongReceipt]))
            .Should()
            .BeFalse();

        IsDurableRecoveryEnabled(new RuntimeActorStateSchemaContext(
                WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion,
                [exactReceipt, activationSealReceipt]))
            .Should()
            .BeTrue();
        IsDurableRecoveryEnabled(new RuntimeActorStateSchemaContext(
                WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion,
                [exactReceipt]))
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task WorkflowScopeMigration_ShouldCloneStateAndCreateExactAdoptionReceipt()
    {
        var implementation = ResolveImplementation();
        var original = new ProjectionScopeState
        {
            RootActorId = "workflow-run-alpha",
            ProjectionKind = "workflow-execution",
            Active = true,
            LastSuccessfulVersion = 7,
        };
        var snapshot = original.ToByteArray();
        var membership = CurrentMembership();

        var decision = await RuntimeActorStateMigrationAdmission.EvaluateAsync(
            new RuntimeActorIdentity
            {
                Kind = WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                StateSchemaVersion = 0,
            },
            typeof(ProjectionScopeState).FullName,
            snapshot,
            implementation,
            new StubAdmissionReader(CreateGraphAdmission(), CreateActivationSealAdmission()),
            new StubMembershipReader(membership),
            new FixedTimeProvider(Now));

        decision.IsMigrationRequired.Should().BeTrue();
        decision.IsAdmitted.Should().BeTrue();
        decision.StateSchemaVersion.Should().Be(
            WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion);
        decision.StateTypeName.Should().Be(typeof(ProjectionScopeState).FullName);
        decision.Snapshot.Should().Equal(snapshot);
        decision.AdmissionMembership.Should().Be(membership);

        var receipts = decision.AdoptionReceipts.Should().HaveCount(2).And.Subject;
        var graphReceipt = receipts.Single(static receipt => receipt.StateSchemaVersion == 1);
        graphReceipt.RequiredCapability.Should().Be(RuntimeFleetCapability.ProjectionIncrementalGraphV1);
        graphReceipt.RequiredContractId.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1);
        graphReceipt.RequiredContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion);
        graphReceipt.CapabilityEpoch.Should().Be(3);
        graphReceipt.AuthorityStateVersion.Should().Be(9);
        graphReceipt.MembershipEpoch.Should().Be(7);
        graphReceipt.DeploymentRevision.Should().Be("revision-a");
        graphReceipt.AuthorityActorId.Should().Be(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        graphReceipt.MembershipDigest.Should().Be("digest-a");
        graphReceipt.EvidenceStatus.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
        graphReceipt.AdoptedAt.ToDateTimeOffset().Should().Be(Now);

        var activationSealReceipt = receipts.Single(static receipt => receipt.StateSchemaVersion == 2);
        activationSealReceipt.RequiredCapability.Should()
            .Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
        activationSealReceipt.RequiredContractId.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1);
        activationSealReceipt.RequiredContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion);
        activationSealReceipt.CapabilityEpoch.Should().Be(4);
        activationSealReceipt.AuthorityStateVersion.Should().Be(10);
        activationSealReceipt.EvidenceStatus.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
        activationSealReceipt.AdoptedAt.ToDateTimeOffset().Should().Be(Now);
    }

    [Fact]
    public async Task WorkflowScopeActivationSealMigration_ShouldFailClosedWithoutFreshV3Admission()
    {
        var implementation = ResolveImplementation();
        var snapshot = new ProjectionScopeState
        {
            RootActorId = "workflow-run-alpha",
            ProjectionKind = "workflow-execution",
            Active = true,
        }.ToByteArray();
        var identity = new RuntimeActorIdentity
        {
            Kind = WorkflowExecutionMaterializationScopeGAgent.AgentKind,
            StateSchemaVersion =
                WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion,
        };
        identity.StateSchemaAdoptions.Add(CreateGraphAdoptionReceipt());

        var blocked = await RuntimeActorStateMigrationAdmission.EvaluateAsync(
            identity,
            typeof(ProjectionScopeState).FullName,
            snapshot,
            implementation,
            new StubAdmissionReader(CreateGraphAdmission()),
            new StubMembershipReader(CurrentMembership()),
            new FixedTimeProvider(Now));

        blocked.IsMigrationRequired.Should().BeTrue();
        blocked.IsAdmitted.Should().BeFalse();
        blocked.AdoptionReceipts.Should().BeEmpty();

        var admitted = await RuntimeActorStateMigrationAdmission.EvaluateAsync(
            identity,
            typeof(ProjectionScopeState).FullName,
            snapshot,
            implementation,
            new StubAdmissionReader(CreateActivationSealAdmission()),
            new StubMembershipReader(CurrentMembership()),
            new FixedTimeProvider(Now));

        admitted.IsAdmitted.Should().BeTrue();
        admitted.StateSchemaVersion.Should().Be(
            WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion);
        var receipt = admitted.AdoptionReceipts.Should().ContainSingle().Subject;
        receipt.StateSchemaVersion.Should().Be(2);
        receipt.RequiredCapability.Should()
            .Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
        receipt.EvidenceStatus.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
    }

    [Fact]
    public async Task WorkflowScopePhaseAReader_WhenPersistedSchemaIsV2_ShouldReject()
    {
        var current = ResolveImplementation();
        var oldReader = current with
        {
            Metadata = current.Metadata with
            {
                StateSchemaVersion =
                    WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion,
            },
            StateMigrations = current.StateMigrations!
                .Where(static migration => migration.ToStateVersion == 1)
                .ToArray(),
        };
        var snapshot = new ProjectionScopeState
        {
            RootActorId = "workflow-run-alpha",
            ProjectionKind = "workflow-execution",
        }.ToByteArray();

        var act = () => RuntimeActorStateMigrationAdmission.EvaluateAsync(
            new RuntimeActorIdentity
            {
                Kind = WorkflowExecutionMaterializationScopeGAgent.AgentKind,
                StateSchemaVersion = WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion,
            },
            typeof(ProjectionScopeState).FullName,
            snapshot,
            oldReader,
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*persisted state schema version 2 is newer than supported version 1*");
    }

    private static AgentImplementation ResolveImplementation()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAgentKindRegistry>()
            .Resolve(WorkflowExecutionMaterializationScopeGAgent.AgentKind);
    }

    private static RuntimeFleetCapabilityAdmission CreateGraphAdmission()
    {
        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
            Status = RuntimeFleetCapabilityGateStatus.Open,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 9,
            CapabilityEpoch = 3,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            MinimumReaderContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            MembershipObservedAt = Timestamp.FromDateTimeOffset(Now.AddSeconds(-5)),
            MembershipValidUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(1)),
            ActiveMemberCount = 1,
            ConfirmedMemberCount = 1,
            MembershipDigest = "digest-a",
            ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
        };
        admission.AdmittedMembers.Add(
            new RuntimeFleetAdmittedMember
            {
                MemberId = "member-a",
                Incarnation = "inc-a",
            });
        return admission;
    }

    private static RuntimeFleetCapabilityAdmission CreateActivationSealAdmission()
    {
        var admission = CreateGraphAdmission();
        admission.Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3;
        admission.ContractId =
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1;
        admission.MinimumReaderContractVersion =
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion;
        admission.CapabilityEpoch = 4;
        admission.AuthorityStateVersion = 10;
        return admission;
    }

    private static RuntimeActorStateSchemaAdoptionReceipt CreateGraphAdoptionReceipt() =>
        new()
        {
            StateSchemaVersion =
                WorkflowExecutionMaterializationScopeGAgent.IncrementalGraphStateSchemaVersion,
            RequiredCapability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
            RequiredContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            RequiredContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            CapabilityEpoch = 3,
            AuthorityStateVersion = 9,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            AdoptedAt = Timestamp.FromDateTimeOffset(Now),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = "digest-a",
            EvidenceStatus = RuntimeFleetCapabilityGateStatus.Open,
        };

    private static RuntimeActorStateSchemaAdoptionReceipt CreateActivationSealAdoptionReceipt() =>
        new()
        {
            StateSchemaVersion = WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion,
            RequiredCapability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
            RequiredContractId =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1,
            RequiredContractVersion =
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion,
            CapabilityEpoch = 4,
            AuthorityStateVersion = 10,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            AdoptedAt = Timestamp.FromDateTimeOffset(Now),
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            MembershipDigest = "digest-a",
            EvidenceStatus = RuntimeFleetCapabilityGateStatus.Open,
        };

    private static bool IsDurableRecoveryEnabled(RuntimeActorStateSchemaContext? context)
    {
        var services = new ServiceCollection();
        if (context != null)
        {
            services.AddSingleton<IRuntimeActorStateSchemaContextReader>(
                new FixedSchemaContextReader(context));
        }

        using var provider = services.BuildServiceProvider();
        var agent = new WorkflowExecutionMaterializationScopeGAgent
        {
            Services = provider,
        };
        return (bool)typeof(WorkflowExecutionMaterializationScopeGAgent)
            .GetProperty(
                "EnablesDurableObservationRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(agent)!;
    }

    private static RuntimeLocalMembershipIdentity CurrentMembership() =>
        new(7, "digest-a", "revision-a", "member-a", "inc-a");

    private sealed class StubAdmissionReader(params RuntimeFleetCapabilityAdmission[] admissions)
        : IRuntimeFleetCapabilityAdmissionReader
    {
        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                admissions.SingleOrDefault(candidate => candidate.Capability == capability)?.Clone());
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

    private sealed class FixedSchemaContextReader(RuntimeActorStateSchemaContext current)
        : IRuntimeActorStateSchemaContextReader
    {
        public RuntimeActorStateSchemaContext? Current { get; } = current;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
