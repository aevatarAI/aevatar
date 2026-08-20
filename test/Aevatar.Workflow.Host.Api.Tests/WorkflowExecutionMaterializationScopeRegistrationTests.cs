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
        var migration = implementation.StateMigrations.Should().ContainSingle().Subject;
        migration.MigrationType.Should()
            .Be(typeof(WorkflowExecutionMaterializationScopeStateV0ToV1Migration));
        migration.FromStateVersion.Should().Be(0);
        migration.ToStateVersion.Should().Be(1);
        migration.RequiredCapability.Should().Be(RuntimeFleetCapability.ProjectionIncrementalGraphV1);
        migration.RequiredContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1);
        migration.RequiredContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion);

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
        var exactReceipt = CreateAdoptionReceipt();

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
            new StubAdmissionReader(CreateAdmission()),
            new StubMembershipReader(membership),
            new FixedTimeProvider(Now));

        decision.IsMigrationRequired.Should().BeTrue();
        decision.IsAdmitted.Should().BeTrue();
        decision.StateSchemaVersion.Should().Be(1);
        decision.StateTypeName.Should().Be(typeof(ProjectionScopeState).FullName);
        decision.Snapshot.Should().Equal(snapshot);
        decision.AdmissionMembership.Should().Be(membership);

        var receipt = decision.AdoptionReceipts.Should().ContainSingle().Subject;
        receipt.StateSchemaVersion.Should().Be(1);
        receipt.RequiredCapability.Should().Be(RuntimeFleetCapability.ProjectionIncrementalGraphV1);
        receipt.RequiredContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1);
        receipt.RequiredContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion);
        receipt.CapabilityEpoch.Should().Be(3);
        receipt.AuthorityStateVersion.Should().Be(9);
        receipt.MembershipEpoch.Should().Be(7);
        receipt.DeploymentRevision.Should().Be("revision-a");
        receipt.AuthorityActorId.Should().Be(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        receipt.MembershipDigest.Should().Be("digest-a");
        receipt.AdoptedAt.ToDateTimeOffset().Should().Be(Now);
    }

    [Fact]
    public async Task WorkflowScopeOldSchemaReader_WhenPersistedSchemaIsV1_ShouldReject()
    {
        var current = ResolveImplementation();
        var oldReader = current with
        {
            Metadata = current.Metadata with { StateSchemaVersion = 0 },
            StateMigrations = [],
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
                StateSchemaVersion = 1,
            },
            typeof(ProjectionScopeState).FullName,
            snapshot,
            oldReader,
            new DenyAllRuntimeFleetCapabilityAdmissionReader(),
            new UnavailableRuntimeLocalMembershipIdentityReader());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*persisted state schema version 1 is newer than supported version 0*");
    }

    private static AgentImplementation ResolveImplementation()
    {
        var services = new ServiceCollection();
        services.AddWorkflowExecutionProjectionCQRS();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAgentKindRegistry>()
            .Resolve(WorkflowExecutionMaterializationScopeGAgent.AgentKind);
    }

    private static RuntimeFleetCapabilityAdmission CreateAdmission()
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

    private static RuntimeActorStateSchemaAdoptionReceipt CreateAdoptionReceipt() =>
        new()
        {
            StateSchemaVersion =
                WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion,
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
