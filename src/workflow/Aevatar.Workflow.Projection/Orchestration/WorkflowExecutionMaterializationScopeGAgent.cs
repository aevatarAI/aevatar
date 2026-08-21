using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Projection.Orchestration;

[GAgent(
    WorkflowExecutionMaterializationScopeGAgent.AgentKind,
    StateSchemaVersion = WorkflowExecutionMaterializationScopeGAgent.SupportedStateSchemaVersion)]
public sealed class WorkflowExecutionMaterializationScopeGAgent
    : ProjectionMaterializationScopeGAgentBase<WorkflowExecutionMaterializationContext>
{
    public const string AgentKind =
        "projection.materialization-scope.workflow-execution-materialization-context";
    public const int IncrementalGraphStateSchemaVersion = 1;
    public const int SupportedStateSchemaVersion = 2;

    protected override bool EnablesDurableObservationRecovery =>
        WorkflowProjectionIncrementalGraphSchemaAdoption.IsGranted(
            Services.GetService<IRuntimeActorStateSchemaContextReader>());

    protected override async ValueTask OnScopeReadyAsync(CancellationToken ct)
    {
        if (!State.Active || State.Released)
            return;

        if (State.ActiveMaterializationRoute == null)
        {
            await PersistDomainEventAsync(new ProjectionMaterializationRouteInitializedEvent
            {
                Route = BuildCompatibilityRoute(),
                OccurredAtUtc = Timestamp.FromDateTimeOffset(
                    (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow()),
            });
        }

        if (!EnablesDurableObservationRecovery)
            return;

        if (WorkflowProjectionGraphRoutePolicy.HasInProgressCutover(State.MaterializationCutover))
        {
            await ScheduleCutoverContinuationAsync(ct);
            return;
        }

        if (WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(State.ActiveMaterializationRoute))
            return;

        await PersistDomainEventAsync(new ProjectionMaterializationCutoverRequestedEvent
        {
            CandidateRoute = BuildIncrementalCandidateRoute(State.ActiveMaterializationRoute!),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(
                (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow()),
        });

        await ScheduleCutoverContinuationAsync(ct);
    }

    protected override ValueTask PrepareObservationContextAsync(
        WorkflowExecutionMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        _ = envelope;
        ct.ThrowIfCancellationRequested();
        if (WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(State.ActiveMaterializationRoute) &&
            !EnablesDurableObservationRecovery)
        {
            throw new InvalidOperationException(
                "The incremental workflow graph route requires the exact adopted scope schema receipt.");
        }

        context.MaterializationRoute = State.ActiveMaterializationRoute?.Clone() ?? BuildCompatibilityRoute();
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnObservationMaterializedAsync(
        WorkflowExecutionMaterializationContext context,
        EventEnvelope envelope,
        ProjectionScopeDispatchResult result,
        CancellationToken ct)
    {
        _ = context;
        _ = envelope;
        _ = result;
        if (State.MaterializationCutover is
            {
                Phase: not ProjectionMaterializationCutoverPhase.Unspecified and
                       not ProjectionMaterializationCutoverPhase.Activated,
            })
        {
            await ScheduleCutoverContinuationAsync(ct);
        }
    }

    [EventHandler]
    public async Task HandleRequestMaterializationCutoverAsync(
        RequestProjectionMaterializationCutoverCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!State.Active || State.Released || !EnablesDurableObservationRecovery)
            return;

        var activeRoute = State.ActiveMaterializationRoute;
        if (activeRoute == null ||
            command.ExpectedActiveRouteEpoch != activeRoute.RouteEpoch)
        {
            return;
        }

        var candidateRoute = command.CandidateRoute ??
            throw new InvalidOperationException("A materialization cutover candidate route is required.");
        WorkflowProjectionGraphRoutePolicy.EnsureCanFollow(activeRoute, candidateRoute);

        var existing = State.MaterializationCutover;
        if (existing is
            {
                Phase: not ProjectionMaterializationCutoverPhase.Unspecified and
                       not ProjectionMaterializationCutoverPhase.Activated,
            })
        {
            if (!WorkflowProjectionGraphRoutePolicy.Equals(existing.CandidateRoute, candidateRoute))
            {
                throw new InvalidOperationException(
                    "Another materialization route cutover is already in progress.");
            }

            await ScheduleCutoverContinuationAsync(CancellationToken.None);
            return;
        }

        await PersistDomainEventAsync(new ProjectionMaterializationCutoverRequestedEvent
        {
            CandidateRoute = candidateRoute.Clone(),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(
                (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow()),
        });
        await ScheduleCutoverContinuationAsync(CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleContinueMaterializationCutoverAsync(
        ContinueProjectionMaterializationCutoverCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!State.Active || State.Released || !EnablesDurableObservationRecovery)
            return;

        var cutover = State.MaterializationCutover;
        var candidateRoute = cutover?.CandidateRoute;
        if (cutover == null ||
            candidateRoute == null ||
            candidateRoute.RouteEpoch != command.ExpectedRouteEpoch)
        {
            return;
        }

        var orchestrator = Services.GetRequiredService<WorkflowProjectionGraphCutoverOrchestrator>();
        var now = (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        switch (cutover.Phase)
        {
            case ProjectionMaterializationCutoverPhase.Requested:
            {
                var candidate = await orchestrator.BuildCandidateAsync(
                    State.RootActorId,
                    State.ProjectionKind,
                    candidateRoute,
                    CancellationToken.None);
                if (candidate == null)
                    return;

                await PersistDomainEventAsync(new ProjectionMaterializationCutoverCandidateBuiltEvent
                {
                    CandidateRoute = candidateRoute.Clone(),
                    CandidateSource = candidate.Source.Clone(),
                    CandidateFingerprint = candidate.Fingerprint,
                    OccurredAtUtc = Timestamp.FromDateTimeOffset(now),
                });
                await ScheduleCutoverContinuationAsync(CancellationToken.None);
                return;
            }
            case ProjectionMaterializationCutoverPhase.CandidateBuilt:
            {
                if (!await orchestrator.VerifyCandidateAsync(
                        State.RootActorId,
                        State.ProjectionKind,
                        candidateRoute,
                        cutover.CandidateSource,
                        cutover.CandidateFingerprint,
                        CancellationToken.None))
                {
                    await RestartCandidateBuildAsync(candidateRoute, now);
                    return;
                }

                await PersistDomainEventAsync(new ProjectionMaterializationCutoverGoldenVerifiedEvent
                {
                    CandidateRoute = candidateRoute.Clone(),
                    CandidateSource = cutover.CandidateSource.Clone(),
                    CandidateFingerprint = cutover.CandidateFingerprint,
                    OccurredAtUtc = Timestamp.FromDateTimeOffset(now),
                });
                await ScheduleCutoverContinuationAsync(CancellationToken.None);
                return;
            }
            case ProjectionMaterializationCutoverPhase.GoldenVerified:
            {
                if (!await orchestrator.VerifyCandidateAsync(
                        State.RootActorId,
                        State.ProjectionKind,
                        candidateRoute,
                        cutover.CandidateSource,
                        cutover.CandidateFingerprint,
                        CancellationToken.None))
                {
                    await RestartCandidateBuildAsync(candidateRoute, now);
                    return;
                }

                var grant = await RuntimeFleetCapabilityAdmissionValidation.GetGrantedAdmissionAsync(
                    RuntimeFleetCapability.ProjectionIncrementalGraphV1,
                    RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
                    RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
                    Services.GetRequiredService<IRuntimeFleetCapabilityAdmissionReader>(),
                    Services.GetRequiredService<IRuntimeLocalMembershipIdentityReader>(),
                    Services.GetService<TimeProvider>(),
                    ct: CancellationToken.None);
                if (grant == null)
                    return;

                var admission = grant.Admission;
                await PersistDomainEventAsync(new ProjectionMaterializationCutoverActivatedEvent
                {
                    Route = candidateRoute.Clone(),
                    Source = cutover.CandidateSource.Clone(),
                    CandidateFingerprint = cutover.CandidateFingerprint,
                    OccurredAtUtc = Timestamp.FromDateTimeOffset(now),
                    ActivationProof = new ProjectionMaterializationActivationProof
                    {
                        AuthorityStateVersion = admission.AuthorityStateVersion,
                        CapabilityEpoch = admission.CapabilityEpoch,
                        MembershipEpoch = admission.MembershipEpoch,
                        MembershipDigest = admission.MembershipDigest,
                        DeploymentRevision = admission.DeploymentRevision,
                        ValidatedAtUtc = Timestamp.FromDateTimeOffset(grant.ValidatedAt),
                        ValidUntilUtc = admission.MembershipValidUntil?.Clone(),
                    },
                });
                return;
            }
            case ProjectionMaterializationCutoverPhase.Activated:
            case ProjectionMaterializationCutoverPhase.Unspecified:
            default:
                return;
        }
    }

    private async Task RestartCandidateBuildAsync(
        ProjectionMaterializationRouteFingerprint candidateRoute,
        DateTimeOffset now)
    {
        await PersistDomainEventAsync(new ProjectionMaterializationCutoverRequestedEvent
        {
            CandidateRoute = candidateRoute.Clone(),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        await ScheduleCutoverContinuationAsync(CancellationToken.None);
    }

    private Task ScheduleCutoverContinuationAsync(CancellationToken ct)
    {
        var routeEpoch = State.MaterializationCutover?.CandidateRoute?.RouteEpoch ?? 0;
        return routeEpoch <= 0
            ? Task.CompletedTask
            : PublishAsync(
                new ContinueProjectionMaterializationCutoverCommand
                {
                    ExpectedRouteEpoch = routeEpoch,
                },
                TopologyAudience.Self,
                ct);
    }

    private static ProjectionMaterializationRouteFingerprint BuildCompatibilityRoute() =>
        new()
        {
            ContractId = WorkflowExecutionGraphConstants.LegacyContractId,
            ContractVersion = WorkflowExecutionGraphConstants.LegacyContractVersion,
            PhysicalNamespace = WorkflowExecutionGraphConstants.Scope,
            RouteEpoch = 1,
        };

    private static ProjectionMaterializationRouteFingerprint BuildIncrementalCandidateRoute(
        ProjectionMaterializationRouteFingerprint activeRoute) =>
        new()
        {
            ContractId = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphV1,
            ContractVersion = RuntimeFleetCapabilityContracts.ProjectionIncrementalGraphReaderVersion,
            PhysicalNamespace = WorkflowExecutionGraphConstants.IncrementalPhysicalNamespace,
            RouteEpoch = Math.Max(1, activeRoute.RouteEpoch) + 1,
        };
}

internal static class WorkflowProjectionGraphRoutePolicy
{
    public static bool HasInProgressCutover(ProjectionMaterializationCutoverState? cutover) =>
        cutover is
        {
            Phase: not ProjectionMaterializationCutoverPhase.Unspecified and
                   not ProjectionMaterializationCutoverPhase.Activated,
        };

    public static void EnsureCanFollow(
        ProjectionMaterializationRouteFingerprint activeRoute,
        ProjectionMaterializationRouteFingerprint candidateRoute)
    {
        ArgumentNullException.ThrowIfNull(activeRoute);
        ArgumentNullException.ThrowIfNull(candidateRoute);
        if (!WorkflowRunIncrementalGraphMaterializer.IsIncrementalRoute(candidateRoute))
        {
            throw new InvalidOperationException(
                "A workflow graph cutover target must be a supported versioned incremental route.");
        }
        if (candidateRoute.RouteEpoch != activeRoute.RouteEpoch + 1)
        {
            throw new InvalidOperationException(
                "A workflow graph cutover target must advance the route epoch by exactly one.");
        }
        if (string.Equals(
                activeRoute.PhysicalNamespace,
                candidateRoute.PhysicalNamespace,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A workflow graph cutover target must use a different physical namespace.");
        }
    }

    public static bool Equals(
        ProjectionMaterializationRouteFingerprint? left,
        ProjectionMaterializationRouteFingerprint? right) =>
        left != null &&
        right != null &&
        left.RouteEpoch == right.RouteEpoch &&
        left.ContractVersion == right.ContractVersion &&
        string.Equals(left.ContractId, right.ContractId, StringComparison.Ordinal) &&
        string.Equals(left.PhysicalNamespace, right.PhysicalNamespace, StringComparison.Ordinal);
}
