using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Foundation.Runtime.Callbacks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Foundation.Core.Tests;

public sealed class RuntimeFleetCapabilityAuthorityTests
{
    [Fact]
    public async Task Reconcile_AcrossRestart_ShouldOpenRevokeAndReopenWithMonotonicEpoch()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var first = await fixture.CreateAuthorityAsync();

        await first.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        fixture.Membership.Current = fixture.CreateMembership(epoch: 2, supportsCapability: false);
        await first.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));
        await first.DeactivateAsync();

        fixture.Membership.Current = fixture.CreateMembership(epoch: 3, supportsCapability: true);
        var restarted = await fixture.CreateAuthorityAsync();
        await restarted.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        var gates = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data =>
                data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor) ||
                data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Select(static data => data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor)
                ? data.Unpack<RuntimeFleetCapabilityGateOpenedEvent>().Gate
                : data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate)
            .Where(static gate =>
                gate.Capability == RuntimeFleetCapability.WorkflowNormalizedStateWritesV1)
            .ToArray();

        gates.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked,
            RuntimeFleetCapabilityGateStatus.Open);
        gates.Select(static gate => gate.CapabilityEpoch).Should().Equal(1, 2, 3);
        gates[^1].MembershipEpoch.Should().Be(3);
    }

    [Fact]
    public async Task Reconcile_DefaultRequirements_ShouldOpenAllManagedCapabilities()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        var opened = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data => data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor))
            .Select(static data => data.Unpack<RuntimeFleetCapabilityGateOpenedEvent>().Gate)
            .ToDictionary(static gate => gate.Capability);

        opened.Keys.Should().BeEquivalentTo(new[]
        {
            RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            RuntimeFleetCapability.ProjectionIncrementalGraphV1,
        });
        opened[RuntimeFleetCapability.WorkflowNormalizedStateWritesV1]
            .RequiredContractId.Should().Be(RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1);
        opened[RuntimeFleetCapability.ProjectionScopeStatusTerminalV2]
            .RequiredContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2);
        opened[RuntimeFleetCapability.ProjectionIncrementalGraphV1]
            .RequiredContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1);
        opened.Values.Should().OnlyContain(static gate =>
            gate.Status == RuntimeFleetCapabilityGateStatus.Open &&
            gate.CapabilityEpoch == 1);
        opened[RuntimeFleetCapability.WorkflowNormalizedStateWritesV1]
            .MinimumReaderContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion);
        opened[RuntimeFleetCapability.ProjectionScopeStatusTerminalV2]
            .MinimumReaderContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);
        opened[RuntimeFleetCapability.ProjectionIncrementalGraphV1]
            .MinimumReaderContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion);

        // Pin the wire values themselves: a silent edit of either constant would otherwise keep
        // these assertions green while changing which fleets are admitted.
        opened[RuntimeFleetCapability.ProjectionScopeStatusTerminalV2]
            .RequiredContractId.Should().Be("aevatar.projection.scope-status-terminal.v2");
        opened[RuntimeFleetCapability.ProjectionScopeStatusTerminalV2]
            .MinimumReaderContractVersion.Should().Be(2);
        opened[RuntimeFleetCapability.WorkflowNormalizedStateWritesV1]
            .RequiredContractId.Should().Be("aevatar.workflow.normalized-state.v1");
        opened[RuntimeFleetCapability.WorkflowNormalizedStateWritesV1]
            .MinimumReaderContractVersion.Should().Be(2);
        opened[RuntimeFleetCapability.ProjectionIncrementalGraphV1]
            .RequiredContractId.Should().Be("aevatar.projection.incremental-graph.v1");
        opened[RuntimeFleetCapability.ProjectionIncrementalGraphV1]
            .MinimumReaderContractVersion.Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_WhenOneActiveMemberIsPhaseUnaware_ShouldLeaveTheTerminalStatusGatesShut()
    {
        // A fleet that still runs a binary which only advertises the phase-unaware v1 status
        // contract must open neither status gate: v2 is not unanimous, and v1 is no longer
        // managed, so a source scope on either binary finds no admission and keeps its writer.
        // This covers only the NEW binary's half of the mixed-fleet claim ("new authority: not
        // managed"); the old binary's half ("old authority: not unanimous") — which is the
        // authority that is actually elected for most of a rolling upgrade — is proven by
        // Reconcile_WhenAPhasedMemberJoinsAPhaseUnawareFleet_ShouldRevokeTheOpenV1StatusGate and
        // Reconcile_WhenAPhaseUnawareAuthorityFirstSeesAMixedFleet_ShouldNeverOpenTheV1StatusGate.
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.PhaseUnaware("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Should().BeEmpty();
        // This authority does not manage v1 at all, so the empty v1 history below only restates
        // the managed set — see the two phase-unaware-authority tests for the v1 leg that the
        // unanimity check has to enforce.
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV1))
            .Should().BeEmpty();

        // The rest of the reconcile is healthy, so the shut status gate is the mixed fleet and
        // not a failed observation.
        (await fixture.ReadGateTransitionsAsync())
            .Select(static gate => (gate.Capability, gate.Status, gate.CapabilityEpoch))
            .Should().BeEquivalentTo(new[]
            {
                (RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                    RuntimeFleetCapabilityGateStatus.Open, 1L),
                (RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                    RuntimeFleetCapabilityGateStatus.Open, 1L),
            });
    }

    [Fact]
    public async Task Reconcile_WhenEveryActiveMemberAdvertisesV2_ShouldOpenTheTerminalStatusV2Gate()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.PhaseUnaware("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Should().BeEmpty();

        // The last phase-unaware binary is upgraded.
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var opened = (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Should().ContainSingle().Subject;
        opened.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
        // The refused reconcile committed nothing for this capability, so the first admitted
        // fleet still opens it at capability epoch 1.
        opened.CapabilityEpoch.Should().Be(1);
        opened.MembershipEpoch.Should().Be(2);
        opened.RequiredContractId.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2);
        opened.MinimumReaderContractVersion.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);
    }

    [Fact]
    public async Task Reconcile_WhenV2RouteAndBridgeContractsAreMixed_ShouldRevokeV2Admission()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var transitions = await fixture.ReadGateTransitionsAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        transitions.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked);
        transitions[^1].RevocationReason.Should().Be("fleet-capability-not-unanimous");
        (await fixture.ReadEventsAsync(RuntimeFleetCapabilityGateQuiescedEvent.Descriptor))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenV2RouteAndBridgeContractsAreMixedInitially_ShouldNeverOpenV2Admission()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Should().BeEmpty();
        (await fixture.ReadEventsAsync(RuntimeFleetCapabilityGateQuiescedEvent.Descriptor))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenEveryV2ReaderIsBridgeRevision_ShouldCommitCompatibilityTombstoneThenQuiesce()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        var transitions = await fixture.ReadGateTransitionsAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        transitions.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Revoked,
            RuntimeFleetCapabilityGateStatus.Quiesced);
        transitions.Select(static gate => gate.CapabilityEpoch).Should().OnlyContain(epoch =>
            epoch == long.MaxValue);
        transitions.Select(static gate => gate.MembershipEpoch).Should().OnlyContain(epoch =>
            epoch == 1);
        transitions.Select(static gate => gate.MinimumReaderContractVersion).Should().OnlyContain(version =>
            version == 3);
        transitions.Select(static gate => gate.QuiescenceReaderContractVersion).Should().OnlyContain(version =>
            version == 3);
        transitions.Select(static gate => gate.RequiredContractId).Should().OnlyContain(contractId =>
            contractId == RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1);
        transitions[0].RevocationReason.Should().Be("quiescence-compatibility-tombstone");
        transitions[1].RevocationReason.Should().BeEmpty();

        var statusEvents = await fixture.ReadEventsAsync(
            RuntimeFleetCapabilityGateOpenedEvent.Descriptor,
            RuntimeFleetCapabilityGateRevokedEvent.Descriptor,
            RuntimeFleetCapabilityGateQuiescedEvent.Descriptor);
        statusEvents
            .Where(static data =>
                data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor) ||
                data.Is(RuntimeFleetCapabilityGateQuiescedEvent.Descriptor))
            .Select(static data => data.TypeUrl).Should().Equal(
            Any.Pack(new RuntimeFleetCapabilityGateRevokedEvent()).TypeUrl,
            Any.Pack(new RuntimeFleetCapabilityGateQuiescedEvent()).TypeUrl);
        statusEvents.Should().NotContain(static data =>
            data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor) &&
            data.Unpack<RuntimeFleetCapabilityGateOpenedEvent>().Gate.Capability ==
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
    }

    [Fact]
    public async Task Reconcile_WithLegacyPhaseBPredeclaration_ShouldFailClosedForV3Contract()
    {
        var fixture = new AuthorityFixture();
        var membership = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        foreach (var member in membership.ActiveMembers)
        {
            member.Capabilities.Add(new RuntimeFleetMemberCapability
            {
                Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
                ReaderContractVersion = 1,
                ContractId = "aevatar.projection.scope-status-terminal.phase-b-predeclared",
            });
        }
        membership.MembershipDigest = RuntimeFleetMembershipDigest.Compute(membership);
        fixture.Membership.Current = membership;
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        authority.State.LastMembershipObservationOutcome.Should().Be(
            RuntimeFleetMembershipObservationOutcome.Observed);
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3))
            .Should().BeEmpty("the predeclared contract is not the activation-seal contract");
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Last().Status.Should().Be(RuntimeFleetCapabilityGateStatus.Quiesced);
    }

    [Fact]
    public async Task Reconcile_SchemaZeroAuthority_ShouldQuiesceV2ButNeverManageV3UntilTurnover()
    {
        var fixture = new AuthorityFixture();
        fixture.SchemaContext.Current = fixture.CreateAuthoritySchemaContext(schemaVersion: 0);
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.PhaseB("silo-a"),
            FleetStatusAdvertisement.PhaseB("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Last().Status.Should().Be(RuntimeFleetCapabilityGateStatus.Quiesced);
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3))
            .Should().BeEmpty("schema zero is the bridge-only bootstrap authority");

        // Production reaches this state by runtime turnover and a migration-backed reactivation.
        fixture.SchemaContext.Current = fixture.CreateAuthoritySchemaContext(schemaVersion: 1);
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        var v3 = (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3))
            .Should().ContainSingle().Subject;
        v3.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
        v3.RequiredContractId.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealV1);
        v3.MinimumReaderContractVersion.Should().Be(
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalActivationSealReaderVersion);
    }

    [Fact]
    public async Task Reconcile_UnanimousV3_ShouldOpenOnlyAfterPriorCommittedV2Quiescence()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.PhaseB("silo-a"),
            FleetStatusAdvertisement.PhaseB("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Last().Status.Should().Be(RuntimeFleetCapabilityGateStatus.Quiesced);
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3))
            .Should().BeEmpty("the current reconcile cannot authorize from an uncommitted candidate gate");

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var v3 = (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3))
            .Should().ContainSingle().Subject;
        v3.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
        v3.CapabilityEpoch.Should().Be(1);
        v3.MembershipEpoch.Should().Be(1);
    }

    [Fact]
    public async Task Reconcile_MixedBridgeAndV3Fleet_ShouldNotOpenV3()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.PhaseB("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenV3MemberCapabilityChanges_ShouldRevokeOpenV3()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.PhaseB("silo-a"),
            FleetStatusAdvertisement.PhaseB("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.PhaseB("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        var transitions = await fixture.ReadGateTransitionsAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
        transitions.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked);
        transitions[^1].RevocationReason.Should().Be("fleet-capability-not-unanimous");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reconcile_WhenV3ContractOrVersionMismatches_ShouldFailClosed(bool wrongContract)
    {
        var fixture = new AuthorityFixture();
        var membership = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.PhaseB("silo-a"),
            FleetStatusAdvertisement.PhaseB("silo-b"));
        var capability = membership.ActiveMembers[1].Capabilities.Single(candidate =>
            candidate.Capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV3);
        if (wrongContract)
            capability.ContractId = "wrong.activation-seal.contract";
        else
            capability.ReaderContractVersion--;
        membership.MembershipDigest = RuntimeFleetMembershipDigest.Compute(membership);
        fixture.Membership.Current = membership;
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV3))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Reconcile_WhenOpenV2BecomesQuiesced_ShouldPublishClosureBeforeBookkeepingAndMembership()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        fixture.Publications.Published.Clear();

        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var publications = fixture.Publications.Published;
        publications[0].StateEvent.EventData
            .Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor).Should().BeTrue();
        publications[1].StateEvent.EventData
            .Is(RuntimeFleetCapabilityGateQuiescedEvent.Descriptor).Should().BeTrue();

        var reconciliationIndex = publications.FindIndex(publication =>
            publication.StateEvent.EventData.Is(RuntimeFleetReconciliationRecordedEvent.Descriptor));
        var membershipIndex = publications.FindIndex(publication =>
            publication.StateEvent.EventData.Is(RuntimeFleetMembershipObservedEvent.Descriptor));
        reconciliationIndex.Should().BeGreaterThan(1);
        membershipIndex.Should().Be(publications.Count - 1);
        membershipIndex.Should().BeGreaterThan(reconciliationIndex);

        var revokedRoot = publications[0].StateRoot
            .Unpack<RuntimeFleetCapabilityAuthorityState>();
        revokedRoot.Gates.Single(gate =>
                gate.Capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV2)
            .Status.Should().Be(RuntimeFleetCapabilityGateStatus.Revoked);
        revokedRoot.Membership.MembershipEpoch.Should().Be(1,
            "the new membership is published only after every gate transition");

        publications.Skip(1).Should().OnlyContain(publication =>
            publication.StateRoot.Unpack<RuntimeFleetCapabilityAuthorityState>().Gates
                .Single(gate =>
                    gate.Capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV2)
                .Status == RuntimeFleetCapabilityGateStatus.Quiesced);
        publications[^1].StateRoot.Unpack<RuntimeFleetCapabilityAuthorityState>()
            .Membership.MembershipEpoch.Should().Be(2);
    }

    [Fact]
    public async Task Reconcile_AfterV2IsQuiesced_ShouldNeverReopenOrRevokeIt()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        // Membership changes and a reader downgrade do not mutate the terminal gate.
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        // Missing evidence revokes ordinary OPEN gates but never the terminal QUIESCED gate.
        fixture.Membership.Failure = new IOException("membership provider unavailable");
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Select(static gate => gate.Status).Should().Equal(
                RuntimeFleetCapabilityGateStatus.Revoked,
                RuntimeFleetCapabilityGateStatus.Quiesced);
    }

    [Fact]
    public async Task PreBridgeAuthority_WhenBridgeContractJoins_ShouldRevokeV2Admission()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        var authority = await fixture.CreateAuthorityAsync(
            AuthorityFixture.PreBridgeV2ManagedCapabilities());

        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Select(static gate => gate.Status).Should().Equal(
                RuntimeFleetCapabilityGateStatus.Open,
                RuntimeFleetCapabilityGateStatus.Revoked);
    }

    [Fact]
    public async Task Reconcile_AcrossPreBridgeAuthorityUpgrade_ShouldPersistRevocationThenQuiescence()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        var preBridgeAuthority = await fixture.CreatePreBridgeAuthorityAsync();

        await preBridgeAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        await preBridgeAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var preBridgeEvents = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .ToList();
        var reconciliationIndex = preBridgeEvents.FindIndex(data =>
            data.Is(RuntimeFleetReconciliationRecordedEvent.Descriptor) &&
            data.Unpack<RuntimeFleetReconciliationRecordedEvent>().DeliveryEnvelopeId ==
            "reconcile-2-schedule-2");
        var membershipIndex = preBridgeEvents.FindIndex(data =>
            data.Is(RuntimeFleetMembershipObservedEvent.Descriptor) &&
            data.Unpack<RuntimeFleetMembershipObservedEvent>().Membership?.MembershipEpoch == 2);
        var revocationIndex = preBridgeEvents.FindIndex(data =>
            data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor) &&
            data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate?.Capability ==
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        reconciliationIndex.Should().BeGreaterThanOrEqualTo(0);
        membershipIndex.Should().BeGreaterThan(reconciliationIndex,
            "the historical authority published the observed membership before gate transitions");
        revocationIndex.Should().BeGreaterThan(membershipIndex,
            "the same-store handoff must preserve the historical reconciliation/membership/gate ordering");
        await preBridgeAuthority.DeactivateAsync();

        var upgradedAuthority = await fixture.CreateAuthorityAsync();
        var recoveredGate = upgradedAuthority.State.Gates.Single(gate =>
            gate.Capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        recoveredGate.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Revoked);
        recoveredGate.CapabilityEpoch.Should().Be(2);
        upgradedAuthority.State.Membership.MembershipEpoch.Should().Be(2);

        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 3,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        await upgradedAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        var transitions = await fixture.ReadGateTransitionsAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        transitions.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked,
            RuntimeFleetCapabilityGateStatus.Revoked,
            RuntimeFleetCapabilityGateStatus.Quiesced);
        transitions.Select(static gate => gate.CapabilityEpoch).Should().Equal(
            1,
            2,
            long.MaxValue,
            long.MaxValue);
        transitions[1].RevocationReason.Should().Be("fleet-capability-not-unanimous");
        transitions[2].RevocationReason.Should().Be("quiescence-compatibility-tombstone");
        transitions[3].RevocationReason.Should().BeEmpty();
        transitions.Skip(2).Should().OnlyContain(gate =>
            gate.MembershipEpoch == 3 &&
            gate.RequiredContractId ==
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1 &&
            gate.QuiescenceReaderContractVersion ==
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion);

        (await fixture.ReadEventsAsync(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Select(static data => data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate)
            .Should().ContainSingle(gate =>
                gate.Capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV2 &&
                gate.RevocationReason == "quiescence-compatibility-tombstone");
        (await fixture.ReadEventsAsync(RuntimeFleetCapabilityGateQuiescedEvent.Descriptor))
            .Select(static data => data.Unpack<RuntimeFleetCapabilityGateQuiescedEvent>().Gate)
            .Should().ContainSingle(gate =>
                gate.Capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV2 &&
                gate.Status == RuntimeFleetCapabilityGateStatus.Quiesced);
    }

    [Fact]
    public async Task PreBridgeReplay_IgnoresTypedQuiescenceButCompatibilityTombstoneFailsClosed()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Bridge("silo-a"),
            FleetStatusAdvertisement.Bridge("silo-b"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        // This is the historical StateTransitionMatcher shape: it knows OPEN/REVOKED and uses
        // OrCurrent for every unknown event. It silently skips the typed event, but the preceding
        // event in the same atomic commit leaves it on the compatibility tombstone.
        var replayed = new RuntimeFleetCapabilityAuthorityState();
        foreach (var data in await fixture.ReadEventsAsync(
                     RuntimeFleetCapabilityGateOpenedEvent.Descriptor,
                     RuntimeFleetCapabilityGateRevokedEvent.Descriptor,
                     RuntimeFleetCapabilityGateQuiescedEvent.Descriptor))
        {
            replayed = StateTransitionMatcher
                .Match(replayed, data)
                .On<RuntimeFleetCapabilityGateOpenedEvent>(ApplyPreBridgeGateTransition)
                .On<RuntimeFleetCapabilityGateRevokedEvent>(ApplyPreBridgeGateTransition)
                .OrCurrent();
        }

        var tombstone = replayed.Gates.Single(gate =>
            gate.Capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        tombstone.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Revoked);
        tombstone.CapabilityEpoch.Should().Be(long.MaxValue);
        tombstone.RevocationReason.Should().Be("quiescence-compatibility-tombstone");

        // The pre-bridge OPEN transition uses this exact checked increment. Even if every member
        // still satisfies reader 2, the tombstone cannot be reopened by an old authority.
        var reopen = () => BuildPreBridgeOpenTransition(tombstone);
        reopen.Should().Throw<OverflowException>();
    }

    [Fact]
    public async Task Reconcile_WhenAPhaseUnawareMemberRejoins_ShouldRevokeTheOpenTerminalStatusV2Gate()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        // A rollback puts a phase-unaware binary back into the fleet.
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.PhaseUnaware("silo-b"));
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var gates = await fixture.ReadGateTransitionsAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2);
        gates.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked);
        gates.Select(static gate => gate.CapabilityEpoch).Should().Equal(1, 2);
        gates[^1].RevocationReason.Should().Be("fleet-capability-not-unanimous");
    }

    [Fact]
    public async Task Reconcile_WhenTheV1StatusCapabilityIsNoLongerManaged_ShouldRevokeItsOpenGate()
    {
        // A gate opened by the previous (phase-unaware) managed set must not survive the binary
        // that stopped managing it: the v1 admission has to disappear before any source scope can
        // be flipped onto the v2 route.
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.PhaseUnaware("silo-a"));
        var phaseUnawareAuthority = await fixture.CreateAuthorityAsync(
            AuthorityFixture.PhaseUnawareManagedCapabilities());
        await phaseUnawareAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV1))
            .Should().ContainSingle().Which.Status.Should()
            .Be(RuntimeFleetCapabilityGateStatus.Open);
        await phaseUnawareAuthority.DeactivateAsync();

        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.Phased("silo-a"));
        var phasedAuthority = await fixture.CreateAuthorityAsync();
        await phasedAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var legacyGates = await fixture.ReadGateTransitionsAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV1);
        legacyGates.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked);
        legacyGates[^1].RevocationReason.Should().Be("capability-no-longer-managed");
        legacyGates[^1].CapabilityEpoch.Should().Be(2);

        var terminalGate = (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Should().ContainSingle().Subject;
        terminalGate.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
        terminalGate.CapabilityEpoch.Should().Be(1);
        terminalGate.MembershipEpoch.Should().Be(2);
    }

    [Fact]
    public async Task Reconcile_WhenAPhasedMemberJoinsAPhaseUnawareFleet_ShouldRevokeTheOpenV1StatusGate()
    {
        // The OTHER half of the mixed-fleet claim, seen from the old binary: for most of a rolling
        // upgrade the elected authority is still a phase-unaware silo, whose managed set contains
        // the v1 status capability. The moment one silo is replaced by a phased binary (which
        // advertises v2 only), that authority must close the open v1 gate as
        // "fleet-capability-not-unanimous" — otherwise a phase-unaware source scope would keep
        // finding a fresh v1 admission and go on adopting v1 status routes while a phased silo is
        // already creating v2 ones.
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.PhaseUnaware("silo-a"),
            FleetStatusAdvertisement.PhaseUnaware("silo-b"));
        var phaseUnawareAuthority = await fixture.CreateAuthorityAsync(
            AuthorityFixture.PhaseUnawareManagedCapabilities());
        await phaseUnawareAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV1))
            .Should().ContainSingle().Which.Status.Should()
            .Be(RuntimeFleetCapabilityGateStatus.Open);

        // silo-b is upgraded: it now advertises the v2 status contract only.
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.PhaseUnaware("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        await phaseUnawareAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var legacyGates = await fixture.ReadGateTransitionsAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV1);
        legacyGates.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked);
        legacyGates[^1].RevocationReason.Should().Be("fleet-capability-not-unanimous");
        legacyGates[^1].CapabilityEpoch.Should().Be(2);
        // A revocation withdraws a named admission: it clones the gate it closes, so the
        // membership proof and contract requirement stay those of the admission being withdrawn
        // (epoch 1 / the v1 contract), and only status, epoch, reason and timestamp move.
        legacyGates[^1].MembershipEpoch.Should().Be(1);
        legacyGates[^1].RequiredContractId.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
        legacyGates[^1].MinimumReaderContractVersion.Should()
            .Be(FleetStatusAdvertisement.PhaseUnawareStatusReaderContractVersion);

        // The old authority does not manage v2, so the mixed fleet admits no status contract at
        // all; the capabilities every member still advertises are untouched by that revocation.
        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2))
            .Should().BeEmpty();
        (await fixture.ReadGateTransitionsAsync())
            .Select(static gate => (gate.Capability, gate.Status, gate.CapabilityEpoch))
            .Should().Equal(
                (RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                    RuntimeFleetCapabilityGateStatus.Open, 1L),
                (RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
                    RuntimeFleetCapabilityGateStatus.Open, 1L),
                (RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                    RuntimeFleetCapabilityGateStatus.Open, 1L),
                (RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
                    RuntimeFleetCapabilityGateStatus.Revoked, 2L),
                (RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                    RuntimeFleetCapabilityGateStatus.Open, 2L),
                (RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                    RuntimeFleetCapabilityGateStatus.Open, 2L));

        // Once the rollout completes the old authority must not resurrect v1 either: no silo
        // advertises it any more, so the gate stays revoked for the rest of this authority's life.
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 3,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.Phased("silo-b"));
        await phaseUnawareAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV1))
            .Select(static gate => gate.Status).Should().Equal(
                RuntimeFleetCapabilityGateStatus.Open,
                RuntimeFleetCapabilityGateStatus.Revoked);
    }

    [Fact]
    public async Task Reconcile_WhenAPhaseUnawareAuthorityFirstSeesAMixedFleet_ShouldNeverOpenTheV1StatusGate()
    {
        // Same rollout state, but the phase-unaware authority is (re)elected after the first
        // phased silo is already up, so there is no open v1 gate to revoke — it must simply never
        // open one. Unlike the current binary's authority this one DOES manage v1, so an empty v1
        // history here is the unanimity check refusing a mixed fleet, not an unmanaged capability.
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 1,
            FleetStatusAdvertisement.Phased("silo-a"),
            FleetStatusAdvertisement.PhaseUnaware("silo-b"));
        var phaseUnawareAuthority = await fixture.CreateAuthorityAsync(
            AuthorityFixture.PhaseUnawareManagedCapabilities());

        await phaseUnawareAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV1))
            .Should().BeEmpty();
        (await fixture.ReadGateTransitionsAsync())
            .Select(static gate => (gate.Capability, gate.Status, gate.CapabilityEpoch))
            .Should().Equal(
                (RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                    RuntimeFleetCapabilityGateStatus.Open, 1L),
                (RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                    RuntimeFleetCapabilityGateStatus.Open, 1L));

        // Proof that the refusal above is the mixed composition and not a fixture that can never
        // satisfy the v1 requirement: roll the phased silo back and the same authority opens v1.
        fixture.Membership.Current = fixture.CreateFleet(
            epoch: 2,
            FleetStatusAdvertisement.PhaseUnaware("silo-a"),
            FleetStatusAdvertisement.PhaseUnaware("silo-b"));
        await phaseUnawareAuthority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        var opened = (await fixture.ReadGateTransitionsAsync(
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV1))
            .Should().ContainSingle().Subject;
        opened.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Open);
        opened.CapabilityEpoch.Should().Be(1);
        opened.MembershipEpoch.Should().Be(2);
        opened.RequiredContractId.Should()
            .Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
        opened.MinimumReaderContractVersion.Should()
            .Be(FleetStatusAdvertisement.PhaseUnawareStatusReaderContractVersion);
    }

    [Fact]
    public async Task Reconcile_WithoutMatchingRuntimeAttestation_ShouldRejectWithoutCommitting()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();

        var forged = fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1");
        fixture.Attestation.Current = null;
        await FluentActions.Awaiting(() => authority.HandleEventAsync(forged))
            .Should().ThrowAsync<InvalidOperationException>();

        fixture.Attestation.Current = new RuntimeFleetReconcileDeliveryAttestation(
            "another-envelope",
            1,
            1,
            RuntimeCallbackSlotEpoch.OrleansSchedulerV2);
        await FluentActions.Awaiting(() => authority.HandleEventAsync(
                forged))
            .Should().ThrowAsync<InvalidOperationException>();

        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(0);
    }

    [Fact]
    public async Task ArbitraryBusinessPayload_ShouldNeverBypassReconcile()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();
        var envelope = Envelope(
            new StringValue { Value = "forged-open" },
            "business-actor",
            RuntimeFleetCapabilityAuthorityIdentity.ActorId);

        await authority.HandleEventAsync(envelope);
        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(0);
    }

    [Fact]
    public async Task Reconcile_DuplicateShouldBeIdempotent_AndStaleSequenceShouldReject()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();
        var accepted = fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1");

        await authority.HandleEventAsync(accepted);
        var committedVersion = await fixture.Store.GetVersionAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        await authority.HandleEventAsync(accepted.Clone());

        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(committedVersion);
        fixture.Scheduler.Acknowledged.Should().HaveCount(2);
        fixture.Scheduler.Acknowledged.Should().OnlyContain(attestation =>
            string.Equals(attestation.EnvelopeId, accepted.Id, StringComparison.Ordinal));
        await FluentActions.Awaiting(() => authority.HandleEventAsync(
                fixture.ReconcileEnvelope(sequence: 1, token: "different-token")))
            .Should().ThrowAsync<InvalidOperationException>();
        fixture.Scheduler.Acknowledged.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reconcile_WhenAcknowledgementFails_ShouldRetryItWithoutRecommitting()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        fixture.Scheduler.AcknowledgementFailures.Enqueue(
            new IOException("scheduler acknowledgement unavailable"));
        var authority = await fixture.CreateAuthorityAsync();
        var accepted = fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1");

        await FluentActions.Awaiting(() => authority.HandleEventAsync(accepted))
            .Should().ThrowAsync<IOException>();
        var committedVersion = await fixture.Store.GetVersionAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        committedVersion.Should().BeGreaterThan(0);
        fixture.Scheduler.AcknowledgeAttempts.Should().Be(1);
        fixture.Scheduler.Acknowledged.Should().BeEmpty();

        await authority.HandleEventAsync(accepted.Clone());

        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(committedVersion);
        fixture.Scheduler.AcknowledgeAttempts.Should().Be(2);
        fixture.Scheduler.Acknowledged.Should().ContainSingle()
            .Which.Should().Be(fixture.Attestation.Current);
    }

    [Fact]
    public async Task Reconcile_WhenMembershipSourceFails_ShouldRevokeOpenGate()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        fixture.Membership.Failure = new IOException("membership provider unavailable");
        fixture.Publications.Published.Clear();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2"));

        fixture.Publications.Published[^1].StateEvent.EventData
            .Is(RuntimeFleetReconciliationRecordedEvent.Descriptor).Should().BeTrue();
        fixture.Publications.Published.Take(fixture.Publications.Published.Count - 1)
            .Should().OnlyContain(publication =>
                publication.StateEvent.EventData.Is(
                    RuntimeFleetCapabilityGateRevokedEvent.Descriptor));

        var revoked = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data => data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Select(static data => data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate)
            .Where(static gate =>
                gate.Capability == RuntimeFleetCapability.WorkflowNormalizedStateWritesV1)
            .Should().ContainSingle().Subject;
        revoked.Status.Should().Be(RuntimeFleetCapabilityGateStatus.Revoked);
        revoked.CapabilityEpoch.Should().Be(2);
        revoked.RevocationReason.Should().Be("membership-source-failed");

        var failureAttempt = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data => data.Is(RuntimeFleetReconciliationRecordedEvent.Descriptor))
            .Select(static data => data.Unpack<RuntimeFleetReconciliationRecordedEvent>())
            .Last();
        failureAttempt.ObservationOutcome.Should()
            .Be(RuntimeFleetMembershipObservationOutcome.SourceFailed);
        failureAttempt.AttemptedAt.Should().Be(Timestamp.FromDateTimeOffset(fixture.Time.GetUtcNow()));

        fixture.Membership.Failure = null;
        fixture.Membership.Current = fixture.CreateMembership(epoch: 2, supportsCapability: true);
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 3, token: "schedule-3"));

        var gates = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Where(static data =>
                data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor) ||
                data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Select(static data => data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor)
                ? data.Unpack<RuntimeFleetCapabilityGateOpenedEvent>().Gate
                : data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate)
            .Where(static gate =>
                gate.Capability == RuntimeFleetCapability.WorkflowNormalizedStateWritesV1)
            .ToArray();
        gates.Select(static gate => gate.Status).Should().Equal(
            RuntimeFleetCapabilityGateStatus.Open,
            RuntimeFleetCapabilityGateStatus.Revoked,
            RuntimeFleetCapabilityGateStatus.Open);
        gates.Select(static gate => gate.CapabilityEpoch).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Reconcile_WhenMembershipChangesBeforeGateTransition_ShouldNotOpenGate()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Reads.Enqueue(
            fixture.CreateMembership(epoch: 1, supportsCapability: true));
        fixture.Membership.Reads.Enqueue(
            fixture.CreateMembership(epoch: 2, supportsCapability: true));
        var authority = await fixture.CreateAuthorityAsync();

        await authority.HandleEventAsync(
            fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));

        var events = (await fixture.Store.GetEventsAsync(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .ToArray();
        events.Should().NotContain(static data =>
            data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor));
        events.Should().NotContain(static data =>
            data.Is(RuntimeFleetMembershipObservedEvent.Descriptor));
        events.Single(static data =>
                data.Is(RuntimeFleetReconciliationRecordedEvent.Descriptor))
            .Unpack<RuntimeFleetReconciliationRecordedEvent>()
            .ObservationOutcome.Should().Be(
                RuntimeFleetMembershipObservationOutcome.RegressedOrConflicted);
    }

    [Fact]
    public async Task Reconcile_WhenMembershipSourceIsCanceled_ShouldPropagateWithoutRevoking()
    {
        var fixture = new AuthorityFixture();
        fixture.Membership.Current = fixture.CreateMembership(epoch: 1, supportsCapability: true);
        var authority = await fixture.CreateAuthorityAsync();
        await authority.HandleEventAsync(fixture.ReconcileEnvelope(sequence: 1, token: "schedule-1"));
        var committedVersion = await fixture.Store.GetVersionAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId);
        fixture.Membership.Failure = new OperationCanceledException("membership read canceled");

        await FluentActions.Awaiting(() => authority.HandleEventAsync(
                fixture.ReconcileEnvelope(sequence: 2, token: "schedule-2")))
            .Should().ThrowAsync<OperationCanceledException>();

        (await fixture.Store.GetVersionAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Should().Be(committedVersion);
        (await fixture.Store.GetEventsAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
            .Select(static stateEvent => stateEvent.EventData)
            .Count(static data => data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor))
            .Should().Be(0);
    }

    [Fact]
    public void RuntimeFleetCapability_WireEncoding_ShouldStayPinned()
    {
        // Every other assertion in this file names the capabilities symbolically, so a renumber in
        // src/Aevatar.Foundation.Abstractions/runtime_fleet_capability.proto would keep the whole
        // suite green while changing what one silo's advertisement decodes to on another binary.
        // The numbers below are the wire contract itself and may never move.
        ((int)RuntimeFleetCapability.Unspecified).Should().Be(0);
        ((int)RuntimeFleetCapability.WorkflowNormalizedStateWritesV1).Should().Be(1);
        ((int)RuntimeFleetCapability.ProjectionScopeStatusTerminalV1).Should().Be(2);
        ((int)RuntimeFleetCapability.ProjectionIncrementalGraphV1).Should().Be(3);
        // V2 was appended out of sequence, after the incremental-graph capability.
        ((int)RuntimeFleetCapability.ProjectionScopeStatusTerminalV2).Should().Be(4);
        ((int)RuntimeFleetCapability.ProjectionScopeStatusTerminalV3).Should().Be(5);
        ((int)RuntimeFleetCapabilityGateStatus.Unspecified).Should().Be(0);
        ((int)RuntimeFleetCapabilityGateStatus.Open).Should().Be(1);
        ((int)RuntimeFleetCapabilityGateStatus.Revoked).Should().Be(2);
        ((int)RuntimeFleetCapabilityGateStatus.Quiesced).Should().Be(3);

        // ... and the field that carries it: RuntimeFleetMemberCapability.capability is field 1
        // (tag 0x08), reader_contract_version field 2 (tag 0x10), contract_id field 3 (tag 0x1A).
        new RuntimeFleetMemberCapability
        {
            Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
        }.ToByteArray().Should().Equal(new byte[] { 0x08, 0x04 });

        // Decoded from raw bytes rather than from a symbol, so this fails on a renumber even
        // though both sides of the comparison would otherwise move together: the advertisement a
        // phase-unaware silo puts on the wire must still decode to the v1 status capability with
        // exactly the reader contract version the pre-round-4 binary (base 385e40b89, whose
        // ProjectionScopeStatusGAgent.ContractVersion was
        // RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion = 1) sent.
        var phaseUnawareContractId =
            System.Text.Encoding.UTF8.GetBytes("aevatar.projection.scope-status-terminal.v1");
        var phaseUnawareWire = new byte[] { 0x08, 0x02, 0x10, 0x01, 0x1A, (byte)phaseUnawareContractId.Length }
            .Concat(phaseUnawareContractId)
            .ToArray();
        var decoded = RuntimeFleetMemberCapability.Parser.ParseFrom(phaseUnawareWire);
        decoded.Capability.Should().Be(RuntimeFleetCapability.ProjectionScopeStatusTerminalV1);
        decoded.ReaderContractVersion.Should()
            .Be(FleetStatusAdvertisement.PhaseUnawareStatusReaderContractVersion);
        decoded.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);

        // The historical advertisement and the historical managed requirement are the same triple;
        // AuthorityFixture.PhaseUnawareManagedCapabilities cannot drift away from it.
        var phaseUnaware = FleetStatusAdvertisement.PhaseUnaware("silo-a");
        (phaseUnaware.StatusCapability, phaseUnaware.StatusContractId, phaseUnaware.StatusReaderContractVersion)
            .Should().Be((
                decoded.Capability,
                decoded.ContractId,
                decoded.ReaderContractVersion));
        AuthorityFixture.PhaseUnawareManagedCapabilities().ManagedCapabilities
            .Should().ContainSingle(requirement =>
                requirement.Capability == decoded.Capability &&
                requirement.ContractId == decoded.ContractId &&
                requirement.MinimumReaderContractVersion == decoded.ReaderContractVersion);
    }

    [Fact]
    public void Constructor_WhenFreshnessOptionsAreInvalid_ShouldReject()
    {
        var membership = new MutableMembershipSource();
        var scheduler = new RecordingFleetReconcileScheduleOwner();
        var attestation = new MutableFleetReconcileAttestationReader();

        var zeroTtl = () => new RuntimeFleetCapabilityAuthorityGAgent(
            membership,
            scheduler,
            attestation,
            options: new RuntimeFleetCapabilityAuthorityOptions
            {
                MaxMembershipEvidenceTtl = TimeSpan.Zero,
            });
        var negativeSkew = () => new RuntimeFleetCapabilityAuthorityGAgent(
            membership,
            scheduler,
            attestation,
            options: new RuntimeFleetCapabilityAuthorityOptions
            {
                MaxClockSkew = TimeSpan.FromMilliseconds(-1),
            });

        zeroTtl.Should().Throw<ArgumentOutOfRangeException>();
        negativeSkew.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static RuntimeFleetCapabilityAuthorityState ApplyPreBridgeGateTransition(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetCapabilityGateOpenedEvent evt) =>
        ReplacePreBridgeGate(state, evt.Gate);

    private static RuntimeFleetCapabilityAuthorityState ApplyPreBridgeGateTransition(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetCapabilityGateRevokedEvent evt) =>
        ReplacePreBridgeGate(state, evt.Gate);

    private static RuntimeFleetCapabilityAuthorityState ReplacePreBridgeGate(
        RuntimeFleetCapabilityAuthorityState state,
        RuntimeFleetCapabilityGateState? gate)
    {
        if (gate == null)
            return state;

        var next = state.Clone();
        var current = next.Gates.SingleOrDefault(candidate =>
            candidate.Capability == gate.Capability);
        if (current != null)
            next.Gates.Remove(current);
        next.Gates.Add(gate.Clone());
        return next;
    }

    private static RuntimeFleetCapabilityGateOpenedEvent BuildPreBridgeOpenTransition(
        RuntimeFleetCapabilityGateState current) =>
        new()
        {
            Gate = new RuntimeFleetCapabilityGateState
            {
                Capability = current.Capability,
                Status = RuntimeFleetCapabilityGateStatus.Open,
                CapabilityEpoch = checked(current.CapabilityEpoch + 1),
            },
        };

    private sealed class AuthorityFixture
    {
        internal readonly InMemoryEventStore Store = new();
        internal readonly RecordingCommittedStatePublisher Publications = new();
        internal readonly MutableMembershipSource Membership = new();
        internal readonly RecordingFleetReconcileScheduleOwner Scheduler = new();
        internal readonly MutableFleetReconcileAttestationReader Attestation = new();
        internal readonly MutableAuthoritySchemaContextReader SchemaContext = new();
        internal readonly ManualTimeProvider Time = new(
            new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero));

        internal AuthorityFixture()
        {
            SchemaContext.Current = CreateAuthoritySchemaContext(schemaVersion: 1);
        }

        internal async Task<RuntimeFleetCapabilityAuthorityGAgent> CreateAuthorityAsync(
            RuntimeFleetCapabilityAuthorityOptions? options = null)
        {
            var agent = new RuntimeFleetCapabilityAuthorityGAgent(
                Membership,
                Scheduler,
                Attestation,
                Time,
                options)
            {
                Services = new ServiceCollection()
                    .AddSingleton<IActorRuntimeCallbackScheduler>(
                        new RecordingCallbackScheduler())
                    .AddSingleton<IRuntimeActorStateSchemaContextReader>(SchemaContext)
                    .BuildServiceProvider(),
                EventSourcingBehaviorFactory =
                    new DefaultEventSourcingBehaviorFactory<RuntimeFleetCapabilityAuthorityState>(Store),
                CommittedStateEventPublisher = Publications,
            };
            agent.SetId(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
            await agent.ActivateAsync();
            return agent;
        }

        internal RuntimeActorStateSchemaContext CreateAuthoritySchemaContext(
            int schemaVersion)
        {
            IReadOnlyList<RuntimeActorStateSchemaAdoptionReceipt> receipts = schemaVersion == 0
                ? []
                :
                [
                    new RuntimeActorStateSchemaAdoptionReceipt
                    {
                        StateSchemaVersion = 1,
                        RequiredCapability =
                            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
                        RequiredContractId = RuntimeFleetCapabilityContracts
                            .ProjectionScopeStatusTerminalQuiescenceV1,
                        RequiredContractVersion = RuntimeFleetCapabilityContracts
                            .ProjectionScopeStatusTerminalQuiescenceReaderVersion,
                        CapabilityEpoch = long.MaxValue,
                        EvidenceStatus = RuntimeFleetCapabilityGateStatus.Quiesced,
                        QuiescenceTransitionId = "v2-quiesced-transition",
                    },
                ];
            return new RuntimeActorStateSchemaContext(
                RuntimeFleetCapabilityAuthorityIdentity.AgentKind,
                schemaVersion,
                receipts);
        }

        internal async Task<PreBridgeRuntimeFleetCapabilityAuthorityGAgent>
            CreatePreBridgeAuthorityAsync()
        {
            var agent = new PreBridgeRuntimeFleetCapabilityAuthorityGAgent(
                Membership,
                Scheduler,
                Attestation,
                Time)
            {
                Services = new ServiceCollection()
                    .AddSingleton<IActorRuntimeCallbackScheduler>(
                        new RecordingCallbackScheduler())
                    .BuildServiceProvider(),
                EventSourcingBehaviorFactory =
                    new DefaultEventSourcingBehaviorFactory<RuntimeFleetCapabilityAuthorityState>(Store),
                CommittedStateEventPublisher = Publications,
            };
            agent.SetId(RuntimeFleetCapabilityAuthorityIdentity.ActorId);
            await agent.ActivateAsync();
            return agent;
        }

        internal RuntimeFleetMembershipSnapshot CreateMembership(
            long epoch,
            bool supportsCapability)
        {
            var membership = new RuntimeFleetMembershipSnapshot
            {
                MembershipEpoch = epoch,
                DeploymentRevision = $"revision-{epoch}",
                ObservedAt = Timestamp.FromDateTimeOffset(Time.GetUtcNow()),
                ValidUntil = Timestamp.FromDateTimeOffset(Time.GetUtcNow().AddSeconds(30)),
            };
            var member = new RuntimeFleetMember
            {
                MemberId = "silo-a",
                Incarnation = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DeploymentRevision = membership.DeploymentRevision,
            };
            if (supportsCapability)
            {
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                    ReaderContractVersion =
                        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion,
                    ContractId = RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1,
                });
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
                    ReaderContractVersion =
                        RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion,
                    ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2,
                });
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                    ReaderContractVersion =
                        RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
                    ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
                });
            }
            membership.ActiveMembers.Add(member);
            membership.MembershipDigest = RuntimeFleetMembershipDigest.Compute(membership);
            return membership;
        }

        /// <summary>
        /// Builds an active membership whose members differ only in the status contract they
        /// advertise, so a test can compose a fleet that mixes phase-unaware and phased binaries.
        /// </summary>
        internal RuntimeFleetMembershipSnapshot CreateFleet(
            long epoch,
            params FleetStatusAdvertisement[] advertisements)
        {
            var membership = new RuntimeFleetMembershipSnapshot
            {
                MembershipEpoch = epoch,
                DeploymentRevision = $"revision-{epoch}",
                ObservedAt = Timestamp.FromDateTimeOffset(Time.GetUtcNow()),
                ValidUntil = Timestamp.FromDateTimeOffset(Time.GetUtcNow().AddSeconds(30)),
            };
            foreach (var advertisement in advertisements)
            {
                var member = new RuntimeFleetMember
                {
                    MemberId = advertisement.MemberId,
                    Incarnation = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    DeploymentRevision = membership.DeploymentRevision,
                };
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                    ReaderContractVersion =
                        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion,
                    ContractId = RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1,
                });
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = advertisement.StatusCapability,
                    ReaderContractVersion = advertisement.StatusReaderContractVersion,
                    ContractId = advertisement.StatusContractId,
                });
                if (advertisement.SupportsActivationSeal)
                {
                    member.Capabilities.Add(new RuntimeFleetMemberCapability
                    {
                        Capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV3,
                        ReaderContractVersion = RuntimeFleetCapabilityContracts
                            .ProjectionScopeStatusTerminalActivationSealReaderVersion,
                        ContractId = RuntimeFleetCapabilityContracts
                            .ProjectionScopeStatusTerminalActivationSealV1,
                    });
                }
                member.Capabilities.Add(new RuntimeFleetMemberCapability
                {
                    Capability = RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                    ReaderContractVersion =
                        RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
                    ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
                });
                membership.ActiveMembers.Add(member);
            }

            membership.MembershipDigest = RuntimeFleetMembershipDigest.Compute(membership);
            return membership;
        }

        /// <summary>
        /// The managed set of a binary that predates the phased status route: it still manages the
        /// v1 status capability, which the current binary dropped.
        /// </summary>
        internal static RuntimeFleetCapabilityAuthorityOptions PhaseUnawareManagedCapabilities() =>
            new()
            {
                QuiescencePolicies = [],
                ManagedCapabilities =
                [
                    new(
                        RuntimeFleetCapability.WorkflowNormalizedStateWritesV1,
                        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateV1,
                        RuntimeFleetCapabilityContracts.WorkflowNormalizedStateReaderVersion),
                    new(
                        RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
                        RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
                        FleetStatusAdvertisement.PhaseUnawareStatusReaderContractVersion),
                    new(
                        RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                        RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
                        RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion),
                ],
            };

        internal static RuntimeFleetCapabilityAuthorityOptions PreBridgeV2ManagedCapabilities() =>
            new()
            {
                QuiescencePolicies = [],
            };

        /// <summary>
        /// Committed gate transitions in commit order, optionally narrowed to one capability.
        /// </summary>
        internal async Task<IReadOnlyList<RuntimeFleetCapabilityGateState>> ReadGateTransitionsAsync(
            RuntimeFleetCapability? capability = null)
        {
            var gates = (await Store.GetEventsAsync(
                    RuntimeFleetCapabilityAuthorityIdentity.ActorId))
                .Select(static stateEvent => stateEvent.EventData)
                .Where(static data =>
                    data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor) ||
                    data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor) ||
                    data.Is(RuntimeFleetCapabilityGateQuiescedEvent.Descriptor))
                .Select(static data => data.Is(RuntimeFleetCapabilityGateOpenedEvent.Descriptor)
                    ? data.Unpack<RuntimeFleetCapabilityGateOpenedEvent>().Gate
                    : data.Is(RuntimeFleetCapabilityGateRevokedEvent.Descriptor)
                        ? data.Unpack<RuntimeFleetCapabilityGateRevokedEvent>().Gate
                        : data.Unpack<RuntimeFleetCapabilityGateQuiescedEvent>().Gate);
            if (capability != null)
                gates = gates.Where(gate => gate.Capability == capability);
            return gates.ToArray();
        }

        internal async Task<IReadOnlyList<Any>> ReadEventsAsync(
            params Google.Protobuf.Reflection.MessageDescriptor[] descriptors)
        {
            return (await Store.GetEventsAsync(RuntimeFleetCapabilityAuthorityIdentity.ActorId))
                .Select(static stateEvent => stateEvent.EventData)
                .Where(data => descriptors.Any(data.Is))
                .ToArray();
        }

        internal EventEnvelope ReconcileEnvelope(long sequence, string token)
        {
            var envelope = Envelope(
                new RuntimeFleetReconcileRequested(),
                "untrusted-publisher",
                RuntimeFleetCapabilityAuthorityIdentity.ActorId);
            envelope.Id = $"reconcile-{sequence}-{token}";
            var callback = envelope.EnsureRuntime().EnsureCallback();
            callback.CallbackId = RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId;
            callback.Generation = 1;
            callback.FireIndex = sequence;
            callback.SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2;
            Attestation.Current = new RuntimeFleetReconcileDeliveryAttestation(
                envelope.Id,
                callback.Generation,
                callback.FireIndex,
                callback.SlotEpoch);
            return envelope;
        }
    }

    /// <summary>
    /// The authority binary deployed before bridge quiescence existed. Its production policy
    /// managed V2 directly and its matcher understood only OPEN/REVOKED gate events.
    /// </summary>
    private sealed class PreBridgeRuntimeFleetCapabilityAuthorityGAgent
        : GAgentBase<RuntimeFleetCapabilityAuthorityState>
    {
        private static readonly RuntimeFleetCapabilityRequirement[] Requirements =
            AuthorityFixture.PreBridgeV2ManagedCapabilities().ManagedCapabilities.ToArray();

        private readonly IRuntimeFleetMembershipSnapshotSource _membershipSource;
        private readonly IRuntimeFleetReconcileScheduleOwner _scheduleOwner;
        private readonly IRuntimeFleetReconcileDeliveryAttestationReader _attestationReader;
        private readonly TimeProvider _timeProvider;

        internal PreBridgeRuntimeFleetCapabilityAuthorityGAgent(
            IRuntimeFleetMembershipSnapshotSource membershipSource,
            IRuntimeFleetReconcileScheduleOwner scheduleOwner,
            IRuntimeFleetReconcileDeliveryAttestationReader attestationReader,
            TimeProvider timeProvider)
        {
            _membershipSource = membershipSource;
            _scheduleOwner = scheduleOwner;
            _attestationReader = attestationReader;
            _timeProvider = timeProvider;
        }

        [AllEventHandler(Priority = 0, AllowSelfHandling = true)]
        public async Task HandleCommandAsync(EventEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            if (envelope.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) != true)
                return;

            var attestation = _attestationReader.Current ??
                              throw new InvalidOperationException("Pre-bridge reconcile attestation is required.");
            if (!string.Equals(envelope.Id, attestation.EnvelopeId, StringComparison.Ordinal))
                throw new InvalidOperationException("Pre-bridge reconcile attestation must match the envelope.");
            if (string.Equals(State.LastReconcileDeliveryEnvelopeId, attestation.EnvelopeId, StringComparison.Ordinal))
                return;

            var membership = await _membershipSource.GetCurrentAsync(CancellationToken.None) ??
                             throw new InvalidOperationException("Pre-bridge membership is required.");
            var transitionId = $"runtime-fleet-reconcile:{attestation.Generation}:{attestation.FireIndex}:{attestation.EnvelopeId}";
            var now = _timeProvider.GetUtcNow();
            // Keep the production ordering of the pre-bridge binary exactly: reconciliation,
            // membership, then the gate transitions derived from that membership.
            var transitions = new List<IMessage>
            {
                new RuntimeFleetReconciliationRecordedEvent
                {
                    TransitionId = transitionId,
                    DeliveryEnvelopeId = attestation.EnvelopeId,
                    CallbackGeneration = attestation.Generation,
                    CallbackFireIndex = attestation.FireIndex,
                    CallbackSlotEpoch = attestation.SlotEpoch,
                    ObservationOutcome = RuntimeFleetMembershipObservationOutcome.Observed,
                    AttemptedAt = Timestamp.FromDateTimeOffset(now),
                },
                new RuntimeFleetMembershipObservedEvent
                {
                    TransitionId = transitionId,
                    Membership = membership,
                },
            };

            foreach (var requirement in Requirements.OrderBy(static requirement => requirement.Capability))
            {
                var current = State.Gates.SingleOrDefault(gate => gate.Capability == requirement.Capability);
                var supported = membership.ActiveMembers.All(member =>
                    member.Capabilities.Count(candidate =>
                        candidate.Capability == requirement.Capability &&
                        candidate.ReaderContractVersion >= requirement.MinimumReaderContractVersion &&
                        string.Equals(candidate.ContractId, requirement.ContractId, StringComparison.Ordinal)) == 1);

                if (supported)
                {
                    var alreadyOpenForMembership =
                        current?.Status == RuntimeFleetCapabilityGateStatus.Open &&
                        current.MembershipEpoch == membership.MembershipEpoch &&
                        string.Equals(current.MembershipDigest, membership.MembershipDigest, StringComparison.Ordinal) &&
                        string.Equals(current.DeploymentRevision, membership.DeploymentRevision, StringComparison.Ordinal);
                    if (alreadyOpenForMembership)
                        continue;

                    transitions.Add(new RuntimeFleetCapabilityGateOpenedEvent
                    {
                        Gate = BuildPreBridgeGate(
                            requirement,
                            membership,
                            checked((current?.CapabilityEpoch ?? 0) + 1),
                            RuntimeFleetCapabilityGateStatus.Open,
                            transitionId,
                            now),
                    });
                }
                else if (current?.Status == RuntimeFleetCapabilityGateStatus.Open)
                {
                    var revoked = current.Clone();
                    revoked.Status = RuntimeFleetCapabilityGateStatus.Revoked;
                    revoked.CapabilityEpoch = checked(current.CapabilityEpoch + 1);
                    revoked.ChangedAt = Timestamp.FromDateTimeOffset(now);
                    revoked.LastTransitionId = $"{transitionId}:{(int)current.Capability}:revoke";
                    revoked.RevocationReason = "fleet-capability-not-unanimous";
                    transitions.Add(new RuntimeFleetCapabilityGateRevokedEvent { Gate = revoked });
                }
            }

            await PersistDomainEventsAsync(transitions);
        }

        protected override RuntimeFleetCapabilityAuthorityState TransitionState(
            RuntimeFleetCapabilityAuthorityState current,
            IMessage evt) =>
            StateTransitionMatcher
                .Match(current, evt)
                .On<RuntimeFleetReconciliationRecordedEvent>(ApplyPreBridgeReconciliation)
                .On<RuntimeFleetMembershipObservedEvent>(ApplyPreBridgeMembership)
                .On<RuntimeFleetCapabilityGateOpenedEvent>(ApplyPreBridgeGateTransition)
                .On<RuntimeFleetCapabilityGateRevokedEvent>(ApplyPreBridgeGateTransition)
                .OrCurrent();

        protected override async Task OnActivateAsync(CancellationToken ct)
        {
            if (!string.Equals(Id, RuntimeFleetCapabilityAuthorityIdentity.ActorId, StringComparison.Ordinal))
                throw new InvalidOperationException("Pre-bridge authority identity is fixed.");
            await _scheduleOwner.EnsureScheduledAsync(ct);
        }

        private static RuntimeFleetCapabilityGateState BuildPreBridgeGate(
            RuntimeFleetCapabilityRequirement requirement,
            RuntimeFleetMembershipSnapshot membership,
            long capabilityEpoch,
            RuntimeFleetCapabilityGateStatus status,
            string transitionId,
            DateTimeOffset now) =>
            new()
            {
                Capability = requirement.Capability,
                Status = status,
                CapabilityEpoch = capabilityEpoch,
                MembershipEpoch = membership.MembershipEpoch,
                MembershipDigest = membership.MembershipDigest,
                DeploymentRevision = membership.DeploymentRevision,
                MinimumReaderContractVersion = requirement.MinimumReaderContractVersion,
                RequiredContractId = requirement.ContractId,
                ChangedAt = Timestamp.FromDateTimeOffset(now),
                LastTransitionId = $"{transitionId}:{(int)requirement.Capability}:open",
            };

        private static RuntimeFleetCapabilityAuthorityState ApplyPreBridgeReconciliation(
            RuntimeFleetCapabilityAuthorityState state,
            RuntimeFleetReconciliationRecordedEvent evt)
        {
            var next = state.Clone();
            next.LastReconcileTransitionId = evt.TransitionId ?? string.Empty;
            next.LastReconcileDeliveryEnvelopeId = evt.DeliveryEnvelopeId ?? string.Empty;
            next.LastReconcileCallbackGeneration = evt.CallbackGeneration;
            next.LastReconcileCallbackFireIndex = evt.CallbackFireIndex;
            next.LastReconcileCallbackSlotEpoch = evt.CallbackSlotEpoch;
            next.LastMembershipObservationOutcome = evt.ObservationOutcome;
            next.LastReconcileAttemptedAt = evt.AttemptedAt?.Clone();
            return next;
        }

        private static RuntimeFleetCapabilityAuthorityState ApplyPreBridgeMembership(
            RuntimeFleetCapabilityAuthorityState state,
            RuntimeFleetMembershipObservedEvent evt)
        {
            var next = state.Clone();
            next.Membership = evt.Membership?.Clone();
            next.LastMembershipTransitionId = evt.TransitionId ?? string.Empty;
            return next;
        }
    }

    private sealed class RecordingCommittedStatePublisher : ICommittedStateEventPublisher
    {
        internal List<CommittedStateEventPublished> Published { get; } = [];

        public Task PublishAsync(
            CommittedStateEventPublished evt,
            ObserverAudience audience = ObserverAudience.CommittedFacts,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add(evt.Clone());
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The status-contract advertisement of one silo: a phase-unaware binary advertises only the
    /// v1 terminal status contract, a phased binary only the v2 one.
    /// </summary>
    private sealed record FleetStatusAdvertisement(
        string MemberId,
        RuntimeFleetCapability StatusCapability,
        string StatusContractId,
        int StatusReaderContractVersion,
        bool SupportsActivationSeal = false)
    {
        /// <summary>
        /// Reader contract version the phase-unaware binaries (b64c96a45 / 8d47b5e5c / 416e80f4a /
        /// 59c6b4e9a) advertised for the v1 status contract: at base 385e40b89 the terminal
        /// materializer's ContractVersion was
        /// RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion = 1. It is a
        /// historical fact about those binaries, not a current production concept, so it is
        /// anchored here rather than in <see cref="RuntimeFleetCapabilityContracts"/>, and pinned
        /// against the raw wire bytes by RuntimeFleetCapability_WireEncoding_ShouldStayPinned.
        /// </summary>
        internal const int PhaseUnawareStatusReaderContractVersion = 1;

        internal static FleetStatusAdvertisement PhaseUnaware(string memberId) =>
            new(
                memberId,
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
                FleetStatusAdvertisement.PhaseUnawareStatusReaderContractVersion);

        internal static FleetStatusAdvertisement Phased(string memberId) =>
            new(
                memberId,
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2,
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);

        internal static FleetStatusAdvertisement Bridge(string memberId) =>
            new(
                memberId,
                RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
                RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion);

        internal static FleetStatusAdvertisement PhaseB(string memberId) =>
            Bridge(memberId) with { SupportsActivationSeal = true };
    }

    private sealed class MutableAuthoritySchemaContextReader
        : IRuntimeActorStateSchemaContextReader
    {
        public RuntimeActorStateSchemaContext? Current { get; set; }
    }

    private sealed class MutableMembershipSource : IRuntimeFleetMembershipSnapshotSource
    {
        internal RuntimeFleetMembershipSnapshot? Current { get; set; }

        internal Queue<RuntimeFleetMembershipSnapshot?> Reads { get; } = new();

        internal Exception? Failure { get; set; }

        public Task<RuntimeFleetMembershipSnapshot?> GetCurrentAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null)
                throw Failure;
            var membership = Reads.Count == 0 ? Current : Reads.Dequeue();
            return Task.FromResult(membership?.Clone());
        }
    }

    private sealed class RecordingFleetReconcileScheduleOwner
        : IRuntimeFleetReconcileScheduleOwner
    {
        internal int EnsureCount { get; private set; }

        internal int AcknowledgeAttempts { get; private set; }

        internal Queue<Exception> AcknowledgementFailures { get; } = new();

        internal List<RuntimeFleetReconcileDeliveryAttestation> Acknowledged { get; } = [];

        public Task EnsureScheduledAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCount++;
            return Task.CompletedTask;
        }

        public Task AcknowledgeDeliveryAsync(
            RuntimeFleetReconcileDeliveryAttestation attestation,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(attestation);
            ct.ThrowIfCancellationRequested();
            AcknowledgeAttempts++;
            if (AcknowledgementFailures.TryDequeue(out var failure))
                throw failure;
            Acknowledged.Add(attestation);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableFleetReconcileAttestationReader
        : IRuntimeFleetReconcileDeliveryAttestationReader
    {
        public RuntimeFleetReconcileDeliveryAttestation? Current { get; set; }
    }

    internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }

    internal sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private long _generation;

        internal List<(RuntimeCallbackTimeoutRequest Request, RuntimeCallbackLease Lease)> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var lease = new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                ++_generation,
                RuntimeCallbackBackend.Dedicated)
            {
                SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
            };
            Timeouts.Add((request, lease));
            return Task.FromResult(lease);
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            RuntimeCallbackLease lease,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(
            string actorId,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class RecordingPublisher : IEventPublisher
    {
        internal List<(string TargetActorId, IMessage Message)> Sent { get; } = [];

        internal Exception? Failure { get; set; }

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            ct.ThrowIfCancellationRequested();
            if (Failure != null)
                throw Failure;
            Sent.Add((targetActorId, evt));
            return Task.CompletedTask;
        }
    }

    internal static IServiceProvider BuildServices(IActorRuntimeCallbackScheduler scheduler) =>
        new ServiceCollection()
            .AddSingleton(scheduler)
            .AddSingleton<IActorRuntimeCallbackScheduler>(scheduler)
            .BuildServiceProvider();

    internal static EventEnvelope Envelope(
        IMessage payload,
        string publisherActorId,
        string targetActorId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, targetActorId),
        };
}
