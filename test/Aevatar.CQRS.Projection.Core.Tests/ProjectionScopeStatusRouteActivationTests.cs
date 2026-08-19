using System.Reflection;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Core.DependencyInjection;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.CQRS.Projection.Providers.InMemory.Stores;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Type = System.Type;

namespace Aevatar.CQRS.Projection.Core.Tests;

/// <summary>
/// Source-scope ownership of the status write route (#3476): a durable projection scope actor
/// adopts the terminal status materializer on its own turn once the fleet admits the terminal
/// contract, releases the legacy status shadow, and fences the route by epoch. Also covers the
/// rolling window in which a legacy writer and the terminal writer overlap on one source.
/// </summary>
public sealed class ProjectionScopeStatusRouteActivationTests
{
    private const string RootActorId = "root-actor";
    private const string ProjectionKind = "test-kind";
    private const string TestScopeAgentKind = "projection.materialization-scope.test-context";

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);

    private static readonly string SourceScopeActorId = ProjectionScopeActorId.Build(
        new ProjectionRuntimeScopeKey(RootActorId, ProjectionKind, ProjectionRuntimeMode.DurableMaterialization));

    private static readonly string TerminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(SourceScopeActorId);
    private static readonly string LegacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(SourceScopeActorId);

    // ── A. source scope adoption ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleEnsureAsync_WithFreshGrant_AdoptsTerminalRouteAndReleasesExistingLegacyShadow()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission());
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        // Terminal relay is written on the source scope's OWN stream, as exact evidence.
        var terminalRelay = harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId)).WhoseValue;
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                terminalRelay,
                SourceScopeActorId,
                TerminalActorId,
                ProjectionScopeStatusGAgent.AgentKind)
            .Should().BeTrue();
        terminalRelay.ActivationGeneration.Should().Be(1);

        // Terminal materializer is created by kind and ensured with the exact lifecycle command.
        harness.Runtime.CreatedByKind.Should().ContainSingle()
            .Which.Should().Be((ProjectionScopeStatusGAgent.AgentKind, TerminalActorId));
        var terminalEnsure = harness.DispatchPort.Dispatched
            .Should().ContainSingle(x => x.actorId == TerminalActorId).Subject;
        terminalEnsure.command.Payload!.Is(EnsureProjectionScopeCommand.Descriptor).Should().BeTrue();
        terminalEnsure.command.Payload.Unpack<EnsureProjectionScopeCommand>().Should().Be(
            new EnsureProjectionScopeCommand
            {
                RootActorId = SourceScopeActorId,
                ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
        terminalEnsure.command.Route.PublisherActorId.Should().Be(SourceScopeActorId);
        terminalEnsure.command.Route.Direct.TargetActorId.Should().Be(TerminalActorId);
        terminalEnsure.command.Propagation.CorrelationId.Should().Be(TerminalActorId);

        // Route committed with epoch 1, contract constants, and the activation proof of the grant.
        var activated = harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>()
            .Should().ContainSingle().Subject;
        activated.Route.RouteEpoch.Should().Be(1);
        activated.Route.ContractId.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1);
        activated.Route.ContractVersion.Should().Be(RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion);
        activated.Route.ContractId.Should().Be(ProjectionScopeStatusGAgent.ContractId);
        activated.Route.ContractVersion.Should().Be(ProjectionScopeStatusGAgent.ContractVersion);
        activated.Route.ActivatedAtUtc.Should().Be(Timestamp.FromDateTimeOffset(Now));
        activated.Route.LegacyRouteReleased.Should().BeFalse();
        activated.OccurredAtUtc.Should().Be(Timestamp.FromDateTimeOffset(Now));
        var admission = CreateAdmission();
        activated.Route.ActivationProof.Should().Be(new ProjectionMaterializationActivationProof
        {
            AuthorityStateVersion = admission.AuthorityStateVersion,
            CapabilityEpoch = admission.CapabilityEpoch,
            MembershipEpoch = admission.MembershipEpoch,
            MembershipDigest = admission.MembershipDigest,
            DeploymentRevision = admission.DeploymentRevision,
            ValidatedAtUtc = Timestamp.FromDateTimeOffset(Now),
            ValidUntilUtc = admission.MembershipValidUntil,
        });

        // Legacy shadow released: relay removed from the source stream, Release dispatched
        // because the legacy actor exists, and the release durably recorded at the same epoch.
        harness.Streams.Removed.Should().Contain((SourceScopeActorId, LegacyActorId));
        harness.Streams.Relays.Should().NotContainKey((SourceScopeActorId, LegacyActorId));
        var legacyRelease = harness.DispatchPort.Dispatched
            .Should().ContainSingle(x => x.actorId == LegacyActorId).Subject;
        legacyRelease.command.Payload!.Unpack<ReleaseProjectionScopeCommand>().Should().Be(
            new ReleaseProjectionScopeCommand
            {
                RootActorId = SourceScopeActorId,
                ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            });
        legacyRelease.command.Route.Direct.TargetActorId.Should().Be(LegacyActorId);
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusLegacyRouteReleasedEvent>()
            .Should().ContainSingle().Which.RouteEpoch.Should().Be(1);

        var route = harness.Agent.State.StatusRoute;
        route.Should().NotBeNull();
        route!.RouteEpoch.Should().Be(1);
        route.LegacyRouteReleased.Should().BeTrue();
        ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion)
            .Should().BeTrue();

        // Ordering contract: the route is committed first (it is the fact; relay and materializer
        // are derived from it and healed on activation), then the terminal relay is written and
        // the materializer ensured, then the legacy shadow is released — and all of that happens
        // before this scope's own observation relay (the activation service's evidence) appears,
        // so no activation service can observe a half-adopted scope.
        harness.Journal.Should().Equal(
            $"commit:{ProjectionScopeStartedEvent.Descriptor.Name}",
            $"commit:{ProjectionScopeStatusRouteActivatedEvent.Descriptor.Name}",
            $"relay-upsert:{SourceScopeActorId}->{TerminalActorId}",
            $"actor-create:{ProjectionScopeStatusGAgent.AgentKind}:{TerminalActorId}",
            $"dispatch:{EnsureProjectionScopeCommand.Descriptor.Name}:{TerminalActorId}",
            $"relay-remove:{SourceScopeActorId}->{LegacyActorId}",
            $"dispatch:{ReleaseProjectionScopeCommand.Descriptor.Name}:{LegacyActorId}",
            $"commit:{ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor.Name}",
            $"relay-upsert:{RootActorId}->{SourceScopeActorId}",
            $"commit:{ProjectionObservationAttachmentUpdatedEvent.Descriptor.Name}");
    }

    [Fact]
    public async Task HandleEnsureAsync_WithFreshGrant_WhenLegacyActorDoesNotExist_ReleasesWithoutDispatchingToIt()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission());

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.DispatchPort.Dispatched.Should().ContainSingle()
            .Which.actorId.Should().Be(TerminalActorId, "no Release is sent to a legacy actor that never existed");
        harness.Streams.Removed.Should().Contain((SourceScopeActorId, LegacyActorId));
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusLegacyRouteReleasedEvent>()
            .Should().ContainSingle().Which.RouteEpoch.Should().Be(1);
        harness.Agent.State.StatusRoute!.LegacyRouteReleased.Should().BeTrue();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithFreshGrant_WhenTerminalActorAlreadyExists_EnsuresWithoutRecreatingIt()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission());
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().ContainSingle(x => x.actorId == TerminalActorId)
            .Which.command.Payload!.Is(EnsureProjectionScopeCommand.Descriptor).Should().BeTrue();
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(1);
    }

    [Fact]
    public async Task ActivationPath_WithFreshGrant_AdoptsTerminalRouteForAlreadyActiveScope()
    {
        var harness = SourceScopeHarness.Build(
            admission: CreateAdmission(),
            initialState: BuildActiveSourceState());

        await harness.Agent.ActivateForTestAsync();

        harness.EventSourcing.Committed.Select(static evt => evt.Descriptor.Name).Should().Equal(
            ProjectionScopeStatusRouteActivatedEvent.Descriptor.Name,
            ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor.Name);
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId));
        harness.Runtime.CreatedByKind.Should().ContainSingle()
            .Which.agentKind.Should().Be(ProjectionScopeStatusGAgent.AgentKind);
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(1);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeTrue();
    }

    public enum NoGrantReason
    {
        AdmissionMissing,
        ReadersNotRegistered,
        MembershipMismatch,
        GateRevoked,
        WrongCapability,
        RuntimeServicesNotRegistered,
    }

    [Theory]
    [InlineData(NoGrantReason.AdmissionMissing)]
    [InlineData(NoGrantReason.ReadersNotRegistered)]
    [InlineData(NoGrantReason.MembershipMismatch)]
    [InlineData(NoGrantReason.GateRevoked)]
    [InlineData(NoGrantReason.WrongCapability)]
    [InlineData(NoGrantReason.RuntimeServicesNotRegistered)]
    public async Task HandleEnsureAsync_WithoutFreshGrant_StaysOnLegacyRoute(NoGrantReason reason)
    {
        var harness = reason switch
        {
            NoGrantReason.AdmissionMissing => SourceScopeHarness.Build(admission: null),
            NoGrantReason.ReadersNotRegistered => SourceScopeHarness.Build(registerFleetReaders: false),
            NoGrantReason.RuntimeServicesNotRegistered => SourceScopeHarness.Build(
                admission: CreateAdmission(),
                registerRuntimeServices: false),
            NoGrantReason.MembershipMismatch => SourceScopeHarness.Build(
                admission: CreateAdmission(),
                membership: new RuntimeLocalMembershipIdentity(8, "digest-a", "revision-a", "member-a", "inc-a")),
            NoGrantReason.GateRevoked => SourceScopeHarness.Build(
                admission: CreateAdmission(status: RuntimeFleetCapabilityGateStatus.Revoked)),
            NoGrantReason.WrongCapability => SourceScopeHarness.Build(
                admission: CreateAdmission(capability: RuntimeFleetCapability.ProjectionIncrementalGraphV1)),
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.EventSourcing.Committed.Select(static evt => evt.Descriptor.Name).Should().Equal(
            ProjectionScopeStartedEvent.Descriptor.Name,
            ProjectionObservationAttachmentUpdatedEvent.Descriptor.Name);
        harness.Streams.Relays.Keys.Should().Equal((RootActorId, SourceScopeActorId));
        harness.Streams.Removed.Should().BeEmpty();
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Agent.State.StatusRoute.Should().BeNull();
        harness.Agent.State.Active.Should().BeTrue();
        harness.Agent.State.ObservationAttached.Should().BeTrue();
    }

    [Fact]
    public async Task HandleEnsureAsync_WithoutFreshGrant_SchedulesBackedOffDurableAdoptionRetry()
    {
        // A long-lived scope that activates before the fleet gate opens must not stay on the
        // legacy route until its next activation (which for an always-active scope never comes).
        var harness = SourceScopeHarness.Build(admission: null, registerCallbackScheduler: true);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        var timeout = harness.Callbacks!.Timeouts.Should().ContainSingle().Subject;
        timeout.ActorId.Should().Be(SourceScopeActorId);
        timeout.CallbackId.Should().Be(TestScopeAgent.RetryCallbackId);
        timeout.DueTime.Should().Be(TestScopeAgent.RetryDelays[0]);
        timeout.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>()
            .Attempt.Should().Be(1);
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    [Fact]
    public async Task HandleRetryStatusRouteAdoptionAsync_WhenGateOpenedMeanwhile_AdoptsWithoutFurtherRetry()
    {
        var harness = SourceScopeHarness.Build(admission: null, registerCallbackScheduler: true);
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        var retry = harness.Callbacks!.Timeouts.Single().TriggerEnvelope.Payload!
            .Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>();
        harness.Fleet!.Admission = CreateAdmission();

        await harness.Agent.HandleRetryStatusRouteAdoptionAsync(retry);

        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Should().ContainSingle();
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(1);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeTrue();
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId));
        harness.Callbacks.Timeouts.Should().ContainSingle("adoption succeeded; no further retry is scheduled");
    }

    [Fact]
    public async Task HandleRetryStatusRouteAdoptionAsync_WhileGateStaysClosed_BacksOffThenStops()
    {
        var harness = SourceScopeHarness.Build(admission: null, registerCallbackScheduler: true);
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        var delivered = 0;
        while (harness.Callbacks!.Timeouts.Count > delivered)
        {
            var retry = harness.Callbacks.Timeouts[delivered++].TriggerEnvelope.Payload!
                .Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>();
            await harness.Agent.HandleRetryStatusRouteAdoptionAsync(retry);
        }

        harness.Callbacks.Timeouts.Select(static t => t.DueTime).Should().Equal(TestScopeAgent.RetryDelays);
        harness.Callbacks.Timeouts.Select(static t =>
                t.TriggerEnvelope.Payload!.Unpack<RetryProjectionScopeStatusRouteAdoptionCommand>().Attempt)
            .Should().Equal(Enumerable.Range(1, TestScopeAgent.RetryDelays.Length));
        harness.Agent.State.StatusRoute.Should().BeNull();
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(NoGrantReason.ReadersNotRegistered)]
    [InlineData(NoGrantReason.RuntimeServicesNotRegistered)]
    public async Task HandleEnsureAsync_WhenAdoptionCanNeverSucceedHere_DoesNotScheduleRetry(NoGrantReason reason)
    {
        var harness = reason switch
        {
            NoGrantReason.ReadersNotRegistered => SourceScopeHarness.Build(
                registerFleetReaders: false,
                registerCallbackScheduler: true),
            _ => SourceScopeHarness.Build(
                admission: null,
                registerRuntimeServices: false,
                registerCallbackScheduler: true),
        };

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Callbacks!.Timeouts.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenRouteAlreadyAdopted_IsIdempotent()
    {
        var harness = SourceScopeHarness.Build(admission: CreateAdmission());
        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());
        var committedAfterFirstEnsure = harness.EventSourcing.Committed.Count;
        var dispatchedAfterFirstEnsure = harness.DispatchPort.Dispatched.Count;
        harness.Journal.Clear();

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.EventSourcing.Committed.Should().HaveCount(committedAfterFirstEnsure);
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Should().ContainSingle();
        harness.DispatchPort.Dispatched.Should().HaveCount(dispatchedAfterFirstEnsure);
        harness.Runtime.CreatedByKind.Should().ContainSingle();
        harness.Journal.Should().Equal(
            [
                $"relay-upsert:{SourceScopeActorId}->{TerminalActorId}",
                $"relay-upsert:{RootActorId}->{SourceScopeActorId}",
            ],
            "a second ensure only re-asserts the terminal and observation relays; the adopted route is not re-negotiated");
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(1);
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenRouteAlreadyAdoptedAndTerminalActorMissing_RecreatesItWithoutLifecycleCommand()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        state.StatusRoute.LegacyRouteReleased = true;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.Runtime.CreatedByKind.Should().ContainSingle()
            .Which.Should().Be((ProjectionScopeStatusGAgent.AgentKind, TerminalActorId));
        harness.DispatchPort.Dispatched.Should().BeEmpty(
            "the materializer restarts itself on the first routed publication; the owner only heals relay + existence");
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId));
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Should().BeEmpty();
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(2);
    }

    [Fact]
    public async Task ActivationPath_WhenRouteAlreadyAdopted_HealsLostTerminalRelayWithoutNewRouteDecision()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 4);
        state.StatusRoute.LegacyRouteReleased = true;
        // No fleet readers at all: an adopted route must not depend on a fresh grant to stay alive.
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);

        await harness.Agent.ActivateForTestAsync();

        harness.EventSourcing.Committed.Should().BeEmpty();
        var relay = harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId)).WhoseValue;
        ProjectionScopeObservationRelayBinding.IsExactActivationEvidence(
                relay,
                SourceScopeActorId,
                TerminalActorId,
                ProjectionScopeStatusGAgent.AgentKind)
            .Should().BeTrue();
        harness.Runtime.CreatedByKind.Should().BeEmpty("the materializer already exists");
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Removed.Should().BeEmpty();
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(4);
    }

    [Fact]
    public async Task ActivationPath_WhenRouteAdoptedButLegacyNotReleased_CompletesReleaseWithoutGrant()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 3);
        state.StatusRoute.LegacyRouteReleased = false;
        var harness = SourceScopeHarness.Build(registerFleetReaders: false, initialState: state);
        harness.Runtime.ExistingActorIds.Add(LegacyActorId);
        harness.Runtime.ExistingActorIds.Add(TerminalActorId);
        harness.Streams.Relays[(SourceScopeActorId, LegacyActorId)] = ProjectionScopeObservationRelayBinding.Create(
            SourceScopeActorId,
            LegacyActorId,
            "projection.materialization-scope.projection-scope-status-materialization-context",
            1);

        await harness.Agent.ActivateForTestAsync();

        harness.EventSourcing.Committed.Select(static evt => evt.Descriptor.Name).Should().Equal(
            ProjectionScopeStatusLegacyRouteReleasedEvent.Descriptor.Name);
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusLegacyRouteReleasedEvent>()
            .Single().RouteEpoch.Should().Be(3);
        harness.Streams.Relays.Should().NotContainKey((SourceScopeActorId, LegacyActorId));
        harness.Streams.Relays.Should().ContainKey((SourceScopeActorId, TerminalActorId),
            "an already adopted route re-asserts its terminal relay but never re-ensures the terminal actor");
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().ContainSingle()
            .Which.command.Payload!.Unpack<ReleaseProjectionScopeCommand>().RootActorId.Should().Be(SourceScopeActorId);
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(3);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeTrue();
    }

    [Fact]
    public async Task HandleEnsureAsync_WhenRouteAdoptedButLegacyNotReleased_DoesNotRaiseAnotherActivation()
    {
        var state = BuildActiveSourceState();
        state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        var harness = SourceScopeHarness.Build(admission: CreateAdmission(), initialState: state);

        await harness.Agent.HandleEnsureAsync(BuildDurableEnsureCommand());

        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Should().BeEmpty();
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusLegacyRouteReleasedEvent>()
            .Should().ContainSingle().Which.RouteEpoch.Should().Be(2);
        harness.Agent.State.StatusRoute!.RouteEpoch.Should().Be(2);
        harness.Agent.State.StatusRoute.LegacyRouteReleased.Should().BeTrue();
    }

    [Fact]
    public async Task HandleEnsureAsync_SessionObservationScope_NeverAdoptsTerminalRoute()
    {
        var harness = SourceScopeHarness.Build(
            admission: CreateAdmission(),
            runtimeMode: ProjectionRuntimeMode.SessionObservation,
            scopeActorId: ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
                RootActorId,
                ProjectionKind,
                ProjectionRuntimeMode.SessionObservation,
                "session-1")));

        await harness.Agent.HandleEnsureAsync(new EnsureProjectionScopeCommand
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            SessionId = "session-1",
            Mode = ProjectionScopeMode.SessionObservation,
        });

        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Should().BeEmpty();
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Relays.Keys.Should().OnlyContain(key => key.Source == RootActorId);
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    [Theory]
    [InlineData(ProjectionScopeStatusMaterializationContext.ProjectionKindValue)]
    [InlineData(ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue)]
    public async Task HandleEnsureAsync_StatusWriterScopes_NeverAdoptTerminalRoute(string statusKind)
    {
        var statusScopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            SourceScopeActorId,
            statusKind,
            ProjectionRuntimeMode.DurableMaterialization));
        var harness = SourceScopeHarness.Build(admission: CreateAdmission(), scopeActorId: statusScopeActorId);

        await harness.Agent.HandleEnsureAsync(new EnsureProjectionScopeCommand
        {
            RootActorId = SourceScopeActorId,
            ProjectionKind = statusKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
        });

        harness.Agent.State.Active.Should().BeTrue();
        harness.Agent.State.ProjectionKind.Should().Be(statusKind);
        harness.EventSourcing.Committed.OfType<ProjectionScopeStatusRouteActivatedEvent>().Should().BeEmpty();
        harness.Runtime.CreatedByKind.Should().BeEmpty();
        harness.DispatchPort.Dispatched.Should().BeEmpty();
        harness.Streams.Relays.Keys.Should().Equal((SourceScopeActorId, statusScopeActorId));
        harness.Agent.State.StatusRoute.Should().BeNull();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void Transition_RouteActivatedWithLowerOrEqualEpoch_LeavesRouteUnchanged(long staleEpoch)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        current.StatusRoute.LegacyRouteReleased = true;
        var stale = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(staleEpoch);
        stale.ActivatedAtUtc = Timestamp.FromDateTimeOffset(Now);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = stale,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current, "the epoch fence rejects a stale or replayed activation outright");
        next.StatusRoute!.RouteEpoch.Should().Be(2);
        next.StatusRoute.LegacyRouteReleased.Should().BeTrue();
        next.StatusRoute.ActivatedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Transition_RouteActivatedWithHigherEpoch_MovesRouteAndResetsLegacyRelease()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);
        current.StatusRoute.LegacyRouteReleased = true;
        var newer = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 3);
        newer.LegacyRouteReleased = true; // the event may not smuggle a pre-released flag through
        var occurredAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(1));

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = newer,
            OccurredAtUtc = occurredAt,
        });

        next.Should().NotBeSameAs(current);
        current.StatusRoute.RouteEpoch.Should().Be(2, "appliers never mutate the current state in place");
        next.StatusRoute!.RouteEpoch.Should().Be(3);
        next.StatusRoute.LegacyRouteReleased.Should().BeFalse();
        next.UpdatedAtUtc.Should().Be(occurredAt);
    }

    [Fact]
    public void Transition_RouteActivatedWithoutRoute_LeavesStateUnchanged()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusRouteActivatedEvent
        {
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute.Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Transition_LegacyRouteReleasedForOtherEpoch_LeavesRouteUnchanged(long otherEpoch)
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();
        current.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 2);

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = otherEpoch,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute!.LegacyRouteReleased.Should().BeFalse();
    }

    [Fact]
    public void Transition_LegacyRouteReleasedWithoutRoute_LeavesStateUnchanged()
    {
        var harness = SourceScopeHarness.Build();
        var current = BuildActiveSourceState();

        var next = harness.Agent.Transition(current, new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = 1,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now),
        });

        next.Should().BeSameAs(current);
        next.StatusRoute.Should().BeNull();
    }

    // ── D. rolling / mixed status writers on one source ────────────────────────────────

    [Fact]
    public async Task MixedWriters_GatedLegacyAndTerminal_ProduceMonotonicDocumentWithoutConflict()
    {
        const long flipVersion = 3;
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static d => d.Id);
        var dispatcher = new RecordingStoreDispatcher(store);
        var clock = new FixedProjectionClock(Now);
        var legacyProjector = new ProjectionScopeStatusProjector(dispatcher, clock);
        var legacyContext = new ProjectionScopeStatusMaterializationContext { RootActorId = SourceScopeActorId };
        var terminal = TerminalAgentHarness.Build(dispatcher, clock);

        for (long version = 1; version <= 5; version++)
        {
            var state = BuildSourceStateAtVersion(version, flipVersion);
            await legacyProjector.ProjectAsync(
                legacyContext,
                BuildCommittedSourceEnvelope(LegacyActorId, state, version));
            await terminal.Agent.HandleObservedEnvelopeAsync(
                BuildCommittedSourceEnvelope(TerminalActorId, state, version));
        }

        dispatcher.Writes.Select(static w => (w.Version, w.Disposition)).Should().Equal(
            (1, ProjectionWriteDisposition.Applied),
            (2, ProjectionWriteDisposition.Applied),
            (3, ProjectionWriteDisposition.Applied),
            (4, ProjectionWriteDisposition.Applied),
            (5, ProjectionWriteDisposition.Applied));
        dispatcher.Writes.Take(2).Should().OnlyContain(w => w.Document.StatusRoute == null, "legacy wrote the pre-flip versions");
        dispatcher.Writes.Skip(2).Should().OnlyContain(
            w => w.Document.StatusRoute != null && w.Document.StatusRoute.RouteEpoch == 1,
            "the terminal wrote every version at or above the flip");
        terminal.Agent.State.Active.Should().BeTrue();
        terminal.Agent.State.SourceScopeActorId.Should().Be(SourceScopeActorId);
        terminal.Agent.State.PendingWrite.Should().BeNull();
        terminal.EventSourcing.Committed.OfType<ProjectionScopeStatusTerminalStartedEvent>()
            .Should().ContainSingle("the first routed publication at the flip starts the terminal");

        // Late / replayed deliveries in the rolling window.
        await legacyProjector.ProjectAsync(
            legacyContext,
            BuildCommittedSourceEnvelope(LegacyActorId, BuildSourceStateAtVersion(2, flipVersion), 2));
        await legacyProjector.ProjectAsync(
            legacyContext,
            BuildCommittedSourceEnvelope(LegacyActorId, BuildSourceStateAtVersion(4, flipVersion), 4));
        await terminal.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(TerminalActorId, BuildSourceStateAtVersion(5, flipVersion), 5));
        await terminal.Agent.HandleObservedEnvelopeAsync(
            BuildCommittedSourceEnvelope(TerminalActorId, BuildSourceStateAtVersion(2, flipVersion), 2));

        dispatcher.Writes.Skip(5).Select(static w => (w.Version, w.Disposition)).Should().Equal(
            (2, ProjectionWriteDisposition.Stale),
            (5, ProjectionWriteDisposition.Duplicate));
        dispatcher.Writes.Should().NotContain(w => w.Disposition == ProjectionWriteDisposition.Conflict);
        dispatcher.Writes.Should().NotContain(w => w.Disposition == ProjectionWriteDisposition.Gap);
        terminal.Agent.State.PendingWrite.Should().BeNull();

        var final = await store.GetAsync(SourceScopeActorId);
        final.Should().NotBeNull();
        final!.StateVersion.Should().Be(5);
        final.LastEventId.Should().Be("source-evt-5");
        final.LastSuccessfulVersion.Should().Be(50);
        final.StatusRoute!.RouteEpoch.Should().Be(1);
    }

    [Fact]
    public async Task MixedWriters_UngatedRollingLegacyWriter_SameVersionWritesAreDuplicatesNeverConflicts()
    {
        // An old-binary legacy shadow that predates the route gate keeps writing every version
        // through the shared mapper; the terminal writes from the flip. Whoever lands second at
        // one version must see an exact duplicate, never a conflict.
        const long flipVersion = 3;
        var store = new InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string>(static d => d.Id);
        var dispatcher = new RecordingStoreDispatcher(store);
        var clock = new FixedProjectionClock(Now);
        var terminal = TerminalAgentHarness.Build(dispatcher, clock);

        for (long version = 1; version <= 5; version++)
        {
            var state = BuildSourceStateAtVersion(version, flipVersion);
            var terminalEnvelope = BuildCommittedSourceEnvelope(TerminalActorId, state, version);
            var legacyEnvelope = BuildCommittedSourceEnvelope(LegacyActorId, state, version);
            if (version % 2 == 1)
            {
                await WriteAsUngatedLegacyAsync(dispatcher, legacyEnvelope, clock);
                await terminal.Agent.HandleObservedEnvelopeAsync(terminalEnvelope);
            }
            else
            {
                await terminal.Agent.HandleObservedEnvelopeAsync(terminalEnvelope);
                await WriteAsUngatedLegacyAsync(dispatcher, legacyEnvelope, clock);
            }
        }

        dispatcher.Writes.Select(static w => (w.Version, w.Disposition)).Should().Equal(
            (1, ProjectionWriteDisposition.Applied),
            (2, ProjectionWriteDisposition.Applied),
            (3, ProjectionWriteDisposition.Applied),
            (3, ProjectionWriteDisposition.Duplicate),
            (4, ProjectionWriteDisposition.Applied),
            (4, ProjectionWriteDisposition.Duplicate),
            (5, ProjectionWriteDisposition.Applied),
            (5, ProjectionWriteDisposition.Duplicate));
        dispatcher.Writes.Should().NotContain(w => w.Disposition == ProjectionWriteDisposition.Conflict);
        dispatcher.Writes.Should().NotContain(w => w.Disposition == ProjectionWriteDisposition.Gap);
        terminal.Agent.State.PendingWrite.Should().BeNull("a duplicate is not a rejected write");

        var final = await store.GetAsync(SourceScopeActorId);
        final!.StateVersion.Should().Be(5);
        final.LastSuccessfulVersion.Should().Be(50);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private static EnsureProjectionScopeCommand BuildDurableEnsureCommand() =>
        new()
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
        };

    private static ProjectionScopeState BuildActiveSourceState() =>
        new()
        {
            RootActorId = RootActorId,
            ProjectionKind = ProjectionKind,
            Mode = ProjectionScopeMode.DurableMaterialization,
            Active = true,
            Released = false,
            ObservationAttached = true,
            ActivationGeneration = 1,
        };

    private static ProjectionScopeState BuildSourceStateAtVersion(long version, long flipVersion)
    {
        var state = BuildActiveSourceState();
        state.LastSuccessfulVersion = version * 10;
        state.HighestSeenVersion = version * 10;
        state.SuccessfulMaterializationTotal = version;
        if (version >= flipVersion)
        {
            state.StatusRoute = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(routeEpoch: 1);
            state.StatusRoute.LegacyRouteReleased = true;
        }

        return state;
    }

    private static EventEnvelope BuildCommittedSourceEnvelope(
        string targetActorId,
        ProjectionScopeState state,
        long version)
    {
        var timestamp = Timestamp.FromDateTimeOffset(Now.AddSeconds(version));
        var original = new EventEnvelope
        {
            Id = $"outer-{targetActorId}-{version}",
            Timestamp = timestamp,
            Route = EnvelopeRouteSemantics.CreateObserverPublication(SourceScopeActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    AgentId = SourceScopeActorId,
                    EventId = $"source-evt-{version}",
                    Version = version,
                    Timestamp = timestamp,
                    EventData = Any.Pack(new ProjectionScopeWatermarkAdvancedEvent
                    {
                        LastSuccessfulVersion = state.LastSuccessfulVersion,
                    }),
                },
                StateRoot = Any.Pack(state),
            }),
        };

        return StreamForwardingRules.BuildForwardedEnvelope(
            original,
            sourceStreamId: SourceScopeActorId,
            targetStreamId: targetActorId,
            StreamForwardingMode.HandleThenForward);
    }

    private static async Task WriteAsUngatedLegacyAsync(
        IProjectionWriteDispatcher<ProjectionScopeStatusDocument> dispatcher,
        EventEnvelope envelope,
        IProjectionClock clock)
    {
        CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var state)
            .Should().BeTrue();
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, clock.UtcNow);
        await dispatcher.UpsertAsync(ProjectionScopeStatusDocumentMapper.Map(state!, stateEvent!, updatedAt));
    }

    private static RuntimeFleetCapabilityAdmission CreateAdmission(
        RuntimeFleetCapability capability = RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
        RuntimeFleetCapabilityGateStatus status = RuntimeFleetCapabilityGateStatus.Open)
    {
        var admission = new RuntimeFleetCapabilityAdmission
        {
            Capability = capability,
            Status = status,
            AuthorityActorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            AuthorityStateVersion = 9,
            CapabilityEpoch = 3,
            MembershipEpoch = 7,
            DeploymentRevision = "revision-a",
            MinimumReaderContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion,
            MembershipObservedAt = Timestamp.FromDateTimeOffset(Now.AddSeconds(-5)),
            MembershipValidUntil = Timestamp.FromDateTimeOffset(Now.AddMinutes(1)),
            ActiveMemberCount = 1,
            ConfirmedMemberCount = 1,
            MembershipDigest = "digest-a",
            ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1,
        };
        admission.AdmittedMembers.Add(new RuntimeFleetAdmittedMember
        {
            MemberId = "member-a",
            Incarnation = "inc-a",
        });
        return admission;
    }

    private static void SetAgentId(GAgentBase agent, string id) =>
        typeof(GAgentBase)
            .GetProperty(nameof(GAgentBase.Id), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(agent, id);

    // ── source scope harness ───────────────────────────────────────────────────────────

    private sealed class SourceScopeHarness
    {
        public required TestScopeAgent Agent { get; init; }
        public required TrackingEventSourcing<ProjectionScopeState> EventSourcing { get; init; }
        public required RecordingStreamProvider Streams { get; init; }
        public required RecordingActorRuntime Runtime { get; init; }
        public required RecordingActorDispatchPort DispatchPort { get; init; }
        public required List<string> Journal { get; init; }
        public required MutableFleetAdmissionSource? Fleet { get; init; }
        public required RecordingCallbackScheduler? Callbacks { get; init; }

        public static SourceScopeHarness Build(
            RuntimeFleetCapabilityAdmission? admission = null,
            bool registerFleetReaders = true,
            bool registerRuntimeServices = true,
            RuntimeLocalMembershipIdentity? membership = null,
            ProjectionRuntimeMode runtimeMode = ProjectionRuntimeMode.DurableMaterialization,
            string? scopeActorId = null,
            ProjectionScopeState? initialState = null,
            bool registerCallbackScheduler = false)
        {
            var journal = new List<string>();
            var agent = new TestScopeAgent(runtimeMode);
            SetAgentId(agent, scopeActorId ?? SourceScopeActorId);
            var eventSourcing = new TrackingEventSourcing<ProjectionScopeState>(agent.Transition, journal);
            agent.EventSourcing = eventSourcing;
            if (initialState != null)
                agent.State.MergeFrom(initialState);

            var streams = new RecordingStreamProvider(journal);
            var runtime = new RecordingActorRuntime(journal);
            var dispatchPort = new RecordingActorDispatchPort(journal);
            var services = new ServiceCollection();
            services.AddSingleton<Func<ProjectionRuntimeScopeKey, TestContext>>(
                static _ => new TestContext(RootActorId, ProjectionKind));
            services.AddSingleton<IStreamProvider>(streams);
            services.AddSingleton<IAgentKindRegistry>(new SingleKindRegistry(typeof(TestScopeAgent), TestScopeAgentKind));
            if (registerRuntimeServices)
            {
                services.AddSingleton<IActorRuntime>(runtime);
                services.AddSingleton<IActorDispatchPort>(dispatchPort);
            }

            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            MutableFleetAdmissionSource? fleet = null;
            if (registerFleetReaders)
            {
                fleet = new MutableFleetAdmissionSource(admission, membership);
                services.AddSingleton<IRuntimeFleetCapabilityAdmissionReader>(fleet);
                services.AddSingleton<IRuntimeLocalMembershipIdentityReader>(fleet);
            }

            RecordingCallbackScheduler? callbacks = null;
            if (registerCallbackScheduler)
            {
                callbacks = new RecordingCallbackScheduler(journal);
                services.AddSingleton<IActorRuntimeCallbackScheduler>(callbacks);
            }

            agent.Services = services.BuildServiceProvider();
            return new SourceScopeHarness
            {
                Agent = agent,
                EventSourcing = eventSourcing,
                Streams = streams,
                Runtime = runtime,
                DispatchPort = dispatchPort,
                Journal = journal,
                Fleet = fleet,
                Callbacks = callbacks,
            };
        }
    }

    private sealed class TestScopeAgent : ProjectionScopeGAgentBase<TestContext>
    {
        public static string RetryCallbackId => StatusRouteAdoptionRetryCallbackId;
        public static TimeSpan[] RetryDelays => StatusRouteAdoptionRetryDelays;

        private readonly ProjectionRuntimeMode _runtimeMode;

        public TestScopeAgent(ProjectionRuntimeMode runtimeMode)
        {
            _runtimeMode = runtimeMode;
        }

        protected override ProjectionRuntimeMode RuntimeMode => _runtimeMode;

        public Task ActivateForTestAsync() => OnActivateAsync(CancellationToken.None);

        /// <summary>Exposes the agent's real transition routing so appliers are exercised through it.</summary>
        public ProjectionScopeState Transition(ProjectionScopeState current, IMessage evt) =>
            TransitionState(current, evt);

        protected override ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
            TestContext context,
            EventEnvelope envelope,
            CancellationToken ct) =>
            ValueTask.FromResult(ProjectionScopeDispatchResult.Skip());
    }

    private sealed record TestContext(string RootActorId, string ProjectionKind)
        : IProjectionMaterializationContext;

    // ── terminal materializer harness (used only for the mixed-writer scenarios) ───────

    private sealed class TerminalAgentHarness
    {
        public required ProjectionScopeStatusGAgent Agent { get; init; }
        public required TrackingEventSourcing<ProjectionScopeStatusTerminalState> EventSourcing { get; init; }

        public static TerminalAgentHarness Build(
            IProjectionWriteDispatcher<ProjectionScopeStatusDocument> dispatcher,
            IProjectionClock clock)
        {
            var journal = new List<string>();
            var agent = new ProjectionScopeStatusGAgent();
            SetAgentId(agent, TerminalActorId);
            var eventSourcing = new TrackingEventSourcing<ProjectionScopeStatusTerminalState>(TransitionTerminal, journal);
            agent.EventSourcing = eventSourcing;

            var services = new ServiceCollection();
            services.AddSingleton(dispatcher);
            services.AddSingleton(clock);
            services.AddSingleton<IStreamProvider>(new RecordingStreamProvider(journal));
            services.AddSingleton<IAgentKindRegistry>(
                new SingleKindRegistry(typeof(ProjectionScopeStatusGAgent), ProjectionScopeStatusGAgent.AgentKind));
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            agent.Services = services.BuildServiceProvider();
            return new TerminalAgentHarness { Agent = agent, EventSourcing = eventSourcing };
        }

        private static ProjectionScopeStatusTerminalState TransitionTerminal(
            ProjectionScopeStatusTerminalState current,
            IMessage evt) =>
            evt switch
            {
                ProjectionScopeStatusTerminalStartedEvent started =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyStarted(current, started),
                ProjectionScopeStatusTerminalReleasedEvent released =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyReleased(current, released),
                ProjectionScopeStatusWriteDeferredEvent deferred =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyWriteDeferred(current, deferred),
                ProjectionScopeStatusWriteRecoveredEvent recovered =>
                    ProjectionScopeStatusTerminalStateApplier.ApplyWriteRecovered(current, recovered),
                _ => current,
            };
    }

    // ── fakes ──────────────────────────────────────────────────────────────────────────

    private sealed class TrackingEventSourcing<TState> : IEventSourcingBehavior<TState>
        where TState : class, IMessage<TState>, new()
    {
        private readonly List<IMessage> _pending = [];
        private readonly Func<TState, IMessage, TState> _transition;
        private readonly List<string> _journal;

        public TrackingEventSourcing(Func<TState, IMessage, TState> transition, List<string> journal)
        {
            _transition = transition;
            _journal = journal;
        }

        public List<IMessage> Committed { get; } = [];
        public long CurrentVersion { get; private set; }

        public void RaiseEvent<TEvent>(TEvent evt) where TEvent : IMessage => _pending.Add(evt);

        public Task<EventStoreCommitResult> ConfirmEventsAsync(CancellationToken ct = default)
        {
            var result = new EventStoreCommitResult();
            foreach (var evt in _pending)
            {
                CurrentVersion++;
                Committed.Add(evt);
                _journal.Add($"commit:{evt.Descriptor.Name}");
                result.CommittedEvents.Add(new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Timestamp = Timestamp.FromDateTimeOffset(Now),
                    Version = CurrentVersion,
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                });
            }

            result.LatestVersion = CurrentVersion;
            _pending.Clear();
            return Task.FromResult(result);
        }

        public Task PersistSnapshotAsync(TState currentState, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TState?> ReplayAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<TState?>(null);

        public void DiscardPendingEvents() => _pending.Clear();

        public TState TransitionState(TState current, IMessage evt) => _transition(current, evt);
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly List<string> _journal;

        public RecordingStreamProvider(List<string> journal)
        {
            _journal = journal;
        }

        public Dictionary<(string Source, string Target), StreamForwardingBinding> Relays { get; } = [];
        public List<(string Source, string Target)> Removed { get; } = [];

        public IStream GetStream(string actorId) => new RecordingStream(this, actorId);

        private sealed class RecordingStream : IStream
        {
            private readonly RecordingStreamProvider _owner;

            public RecordingStream(RecordingStreamProvider owner, string streamId)
            {
                _owner = owner;
                StreamId = streamId;
            }

            public string StreamId { get; }

            public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage =>
                throw new NotSupportedException();

            public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
                where T : IMessage, new() =>
                throw new NotSupportedException();

            public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
            {
                binding.SourceStreamId.Should().Be(StreamId, "a stream only owns relays whose source is itself");
                _owner.Relays[(StreamId, binding.TargetStreamId)] = binding;
                _owner._journal.Add($"relay-upsert:{StreamId}->{binding.TargetStreamId}");
                return Task.CompletedTask;
            }

            public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
            {
                _owner.Relays.Remove((StreamId, targetStreamId));
                _owner.Removed.Add((StreamId, targetStreamId));
                _owner._journal.Add($"relay-remove:{StreamId}->{targetStreamId}");
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<StreamForwardingBinding>>(
                    _owner.Relays.Where(kv => kv.Key.Source == StreamId).Select(kv => kv.Value).ToList());
        }
    }

    private sealed class SingleKindRegistry : IAgentKindRegistry
    {
        private readonly Type _agentType;
        private readonly string _kind;

        public SingleKindRegistry(Type agentType, string kind)
        {
            _agentType = agentType;
            _kind = kind;
        }

        public AgentImplementation Resolve(string kind) => throw new UnknownAgentKindException(kind);

        public bool TryResolve(string kind, out AgentImplementation implementation)
        {
            implementation = null!;
            return false;
        }

        public bool TryGetKindForAgentType(Type agentType, out string kind)
        {
            if (agentType == _agentType)
            {
                kind = _kind;
                return true;
            }

            kind = string.Empty;
            return false;
        }

        public bool TryGetKind(AgentImplementation implementation, out string kind)
        {
            kind = string.Empty;
            return false;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly List<string> _journal;

        public RecordingActorRuntime(List<string> journal)
        {
            _journal = journal;
        }

        public HashSet<string> ExistingActorIds { get; } = [];
        public List<(string agentKind, string actorId)> CreatedByKind { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            throw new NotSupportedException("the source scope creates the terminal materializer by kind only");

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default) =>
            throw new NotSupportedException("the source scope creates the terminal materializer by kind only");

        public Task<IActor> CreateByKindAsync(string agentKind, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actorId = id ?? Guid.NewGuid().ToString("N");
            ExistingActorIds.Add(actorId);
            CreatedByKind.Add((agentKind, actorId));
            _journal.Add($"actor-create:{agentKind}:{actorId}");
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(ExistingActorIds.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        private readonly List<string> _journal;

        public RecordingActorDispatchPort(List<string> journal)
        {
            _journal = journal;
        }

        public List<(string actorId, EventEnvelope command)> Dispatched { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatched.Add((actorId, envelope));
            var payloadName = envelope.Payload?.TypeUrl.Split('/')[^1].Split('.')[^1] ?? string.Empty;
            _journal.Add($"dispatch:{payloadName}:{actorId}");
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent => throw new NotSupportedException();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        private readonly List<string> _journal;

        public RecordingCallbackScheduler(List<string> journal)
        {
            _journal = journal;
        }

        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            Timeouts.Add(request);
            _journal.Add($"timeout:{request.CallbackId}:{request.DueTime}");
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                Generation: Timeouts.Count,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableFleetAdmissionSource
        : IRuntimeFleetCapabilityAdmissionReader, IRuntimeLocalMembershipIdentityReader
    {
        public MutableFleetAdmissionSource(
            RuntimeFleetCapabilityAdmission? admission,
            RuntimeLocalMembershipIdentity? membership)
        {
            Admission = admission;
            Membership = membership ?? new RuntimeLocalMembershipIdentity(7, "digest-a", "revision-a", "member-a", "inc-a");
        }

        public RuntimeFleetCapabilityAdmission? Admission { get; set; }
        public RuntimeLocalMembershipIdentity? Membership { get; set; }

        public Task<RuntimeFleetCapabilityAdmission?> GetAsync(
            RuntimeFleetCapability capability,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                capability == RuntimeFleetCapability.ProjectionScopeStatusTerminalV1 ? Admission?.Clone() : null);
        }

        public ValueTask<RuntimeLocalMembershipIdentity?> GetCurrentAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Membership);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FixedProjectionClock : IProjectionClock
    {
        public FixedProjectionClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class RecordingStoreDispatcher : IProjectionWriteDispatcher<ProjectionScopeStatusDocument>
    {
        private readonly InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string> _store;

        public RecordingStoreDispatcher(InMemoryProjectionDocumentStore<ProjectionScopeStatusDocument, string> store)
        {
            _store = store;
        }

        public List<(long Version, ProjectionWriteDisposition Disposition, ProjectionScopeStatusDocument Document)> Writes { get; } = [];

        public async Task<ProjectionWriteResult> UpsertAsync(
            ProjectionScopeStatusDocument readModel,
            CancellationToken ct = default)
        {
            var result = await _store.UpsertAsync(readModel, ct);
            Writes.Add((readModel.StateVersion, result.Disposition, readModel.Clone()));
            return result;
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
