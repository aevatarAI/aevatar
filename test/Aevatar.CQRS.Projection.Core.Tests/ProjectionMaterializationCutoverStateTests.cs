using Aevatar.CQRS.Projection.Core.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Tests;

public sealed class ProjectionMaterializationCutoverStateTests
{
    [Fact]
    public void CutoverState_ShouldSurviveProtobufRoundTripAtEveryPhase()
    {
        var now = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 18, 8, 0, 0, TimeSpan.Zero));
        var candidateRoute = new ProjectionMaterializationRouteFingerprint
        {
            ContractId = "projection.incremental-graph.v1",
            ContractVersion = 1,
            PhysicalNamespace = "workflow-execution-v2",
            RouteEpoch = 2,
        };
        var source = new ProjectionSourceCoordinate
        {
            ActorId = "actor-1",
            StateVersion = 12,
            EventId = "evt-12",
        };
        var state = new ProjectionScopeState
        {
            Active = true,
            ActiveMaterializationRoute = new ProjectionMaterializationRouteFingerprint
            {
                ContractId = "legacy",
                ContractVersion = 1,
                PhysicalNamespace = "workflow-execution",
                RouteEpoch = 1,
            },
        };

        state = RoundTrip(ProjectionScopeStateApplier.ApplyMaterializationCutoverRequested(
            state,
            new ProjectionMaterializationCutoverRequestedEvent
            {
                CandidateRoute = candidateRoute,
                OccurredAtUtc = now,
            }));
        state.MaterializationCutover.Phase.Should().Be(ProjectionMaterializationCutoverPhase.Requested);
        state.MaterializationCutover.CandidateRoute.Should().BeEquivalentTo(candidateRoute);

        state = RoundTrip(ProjectionScopeStateApplier.ApplyMaterializationCutoverCandidateBuilt(
            state,
            new ProjectionMaterializationCutoverCandidateBuiltEvent
            {
                CandidateRoute = candidateRoute,
                CandidateSource = source,
                CandidateFingerprint = "golden-fingerprint",
                OccurredAtUtc = now,
            }));
        state.MaterializationCutover.Phase.Should().Be(ProjectionMaterializationCutoverPhase.CandidateBuilt);
        state.MaterializationCutover.CandidateSource.Should().BeEquivalentTo(source);
        state.MaterializationCutover.CandidateFingerprint.Should().Be("golden-fingerprint");

        state = RoundTrip(ProjectionScopeStateApplier.ApplyMaterializationCutoverGoldenVerified(
            state,
            new ProjectionMaterializationCutoverGoldenVerifiedEvent
            {
                CandidateRoute = candidateRoute,
                CandidateSource = source,
                CandidateFingerprint = "golden-fingerprint",
                OccurredAtUtc = now,
            }));
        state.MaterializationCutover.Phase.Should().Be(ProjectionMaterializationCutoverPhase.GoldenVerified);

        var proof = new ProjectionMaterializationActivationProof
        {
            AuthorityStateVersion = 17,
            CapabilityEpoch = 4,
            MembershipEpoch = 9,
            MembershipDigest = "digest-a",
            DeploymentRevision = "revision-a",
            ValidatedAtUtc = now,
            ValidUntilUtc = Timestamp.FromDateTimeOffset(now.ToDateTimeOffset().AddMinutes(1)),
        };
        state = RoundTrip(ProjectionScopeStateApplier.ApplyMaterializationCutoverActivated(
            state,
            new ProjectionMaterializationCutoverActivatedEvent
            {
                Route = candidateRoute,
                Source = source,
                CandidateFingerprint = "golden-fingerprint",
                ActivationProof = proof,
                OccurredAtUtc = now,
            }));

        state.MaterializationCutover.Phase.Should().Be(ProjectionMaterializationCutoverPhase.Activated);
        state.ActiveMaterializationRoute.Should().BeEquivalentTo(candidateRoute);
        state.MaterializationCutover.ActivationProof.Should().BeEquivalentTo(proof);
    }

    [Fact]
    public void CutoverRequestedAfterGoldenVerification_ShouldResetStaleCandidateEvidence()
    {
        var state = new ProjectionScopeState
        {
            MaterializationCutover = new ProjectionMaterializationCutoverState
            {
                Phase = ProjectionMaterializationCutoverPhase.GoldenVerified,
                CandidateSource = new ProjectionSourceCoordinate
                {
                    ActorId = "actor-1",
                    StateVersion = 12,
                    EventId = "evt-12",
                },
                CandidateFingerprint = "stale",
                ActivationProof = new ProjectionMaterializationActivationProof
                {
                    MembershipEpoch = 3,
                },
            },
        };
        var route = new ProjectionMaterializationRouteFingerprint
        {
            ContractId = "projection.incremental-graph.v1",
            ContractVersion = 1,
            PhysicalNamespace = "workflow-execution-v2",
            RouteEpoch = 2,
        };

        var restarted = ProjectionScopeStateApplier.ApplyMaterializationCutoverRequested(
            state,
            new ProjectionMaterializationCutoverRequestedEvent
            {
                CandidateRoute = route,
            });

        restarted.MaterializationCutover.Phase.Should().Be(ProjectionMaterializationCutoverPhase.Requested);
        restarted.MaterializationCutover.CandidateSource.Should().BeNull();
        restarted.MaterializationCutover.CandidateFingerprint.Should().BeEmpty();
        restarted.MaterializationCutover.ActivationProof.Should().BeNull();
    }

    [Fact]
    public void CutoverActivation_WhenRouteEpochDoesNotAdvance_ShouldRejectRollbackRegression()
    {
        var state = new ProjectionScopeState
        {
            ActiveMaterializationRoute = new ProjectionMaterializationRouteFingerprint
            {
                ContractId = "projection.incremental-graph.v1",
                ContractVersion = 1,
                PhysicalNamespace = "workflow-execution-v2",
                RouteEpoch = 4,
            },
        };
        var activation = new ProjectionMaterializationCutoverActivatedEvent
        {
            Route = new ProjectionMaterializationRouteFingerprint
            {
                ContractId = "projection.incremental-graph.v1",
                ContractVersion = 1,
                PhysicalNamespace = "workflow-execution-v2-rollback",
                RouteEpoch = 3,
            },
        };

        var act = () => ProjectionScopeStateApplier.ApplyMaterializationCutoverActivated(
            state,
            activation);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must advance the actor-owned route epoch*");
    }

    [Fact]
    public void CutoverActivation_WithoutMatchingGoldenEvidence_ShouldReject()
    {
        var state = new ProjectionScopeState
        {
            ActiveMaterializationRoute = new ProjectionMaterializationRouteFingerprint
            {
                RouteEpoch = 4,
            },
            MaterializationCutover = new ProjectionMaterializationCutoverState
            {
                Phase = ProjectionMaterializationCutoverPhase.CandidateBuilt,
            },
        };
        var activation = new ProjectionMaterializationCutoverActivatedEvent
        {
            Route = new ProjectionMaterializationRouteFingerprint
            {
                ContractId = "projection.incremental-graph.v1",
                ContractVersion = 1,
                PhysicalNamespace = "workflow-execution-v2-rollback",
                RouteEpoch = 5,
            },
            Source = new ProjectionSourceCoordinate
            {
                ActorId = "actor-1",
                StateVersion = 12,
                EventId = "evt-12",
            },
            CandidateFingerprint = "fingerprint",
            ActivationProof = new ProjectionMaterializationActivationProof(),
        };

        var act = () => ProjectionScopeStateApplier.ApplyMaterializationCutoverActivated(
            state,
            activation);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*matching golden-verified candidate evidence*");
    }

    private static ProjectionScopeState RoundTrip(ProjectionScopeState state) =>
        ProjectionScopeState.Parser.ParseFrom(state.ToByteArray());
}
