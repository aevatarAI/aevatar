using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// The source projection scope is the durable per-source owner of its status write route
/// (<see cref="ProjectionScopeState.StatusRoute"/>): which writer may write
/// <see cref="ProjectionScopeStatusDocument"/> for it, under which monotonic route epoch, and in
/// which cutover phase. Moving the writer is a phased cutover executed on the scope's own turn:
/// <list type="number">
/// <item>WARMING — the new writer is installed on this scope's stream and observes; the current
/// writer keeps writing; the new writer reports its observed version.</item>
/// <item>caught up — the reported version reached the version at which warming started.</item>
/// <item>BLOCKED — the scope stops consuming observations; nothing new is published.</item>
/// <item>previous writer released — its relay is removed and its scope released (drained: its
/// queued work is at or below the blocked version).</item>
/// <item>ACTIVE — the route is flipped; the new writer performs the epoch-fenced same-version
/// takeover of the status document and then writes every later terminal outcome.</item>
/// </list>
/// Every phase is a committed fact, so a restart between any two phases resumes the cutover on
/// activation, before this scope's own observation relay is asserted. Fleet capability is only
/// the admission evidence for adopting the terminal writer; a revoked gate rolls the route back
/// to the legacy writer through the same phases (the legacy shadow warms, catches up, takes
/// over). Adoption is decided only on the cold ensure / activation path and on durable retries,
/// never per observed envelope.
/// </summary>
public abstract partial class ProjectionScopeGAgentBase<TContext>
{
    internal const string StatusRouteAdoptionRetryCallbackId = "projection-scope-status-route-adoption";

    /// <summary>
    /// Backed-off retry schedule for adopting the terminal status route after a scope activated
    /// while the fleet gate was still closed. The last delay repeats for as long as the gate
    /// stays closed.
    /// </summary>
    internal static readonly TimeSpan[] StatusRouteAdoptionRetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8),
    ];

    private bool OwnsStatusRoute =>
        State.Active &&
        !State.Released &&
        RuntimeMode == ProjectionRuntimeMode.DurableMaterialization &&
        // Status writers have no status writer of their own (no mirror of the mirror).
        !DependencyInjection.ProjectionScopeStatusRuntimeRegistration.IsProjectionScopeStatusKind(State.ProjectionKind);

    private long CurrentScopeVersion => EventSourcing?.CurrentVersion ?? 0;

    /// <summary>
    /// Continues the status route from its committed phase: restart-safe and idempotent. Called
    /// on cold ensure and on activation before this scope's own observation relay is asserted,
    /// and by the durable adoption retry.
    /// </summary>
    private async Task AdvanceStatusRouteAsync(CancellationToken ct, int attempt = 0)
    {
        if (!OwnsStatusRoute)
            return;

        var route = State.StatusRoute;
        if (ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route))
        {
            switch (route!.Phase)
            {
                case ProjectionScopeStatusRoutePhase.Warming:
                    await ContinueWarmingAsync(route, attempt, ct);
                    return;
                case ProjectionScopeStatusRoutePhase.Blocked:
                    await CompleteCutoverAsync(route, ct);
                    return;
                default:
                    await MaintainActiveTerminalRouteAsync(route, ct);
                    return;
            }
        }

        if (ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route))
        {
            switch (route!.Phase)
            {
                case ProjectionScopeStatusRoutePhase.Warming:
                    await ContinueWarmingAsync(route, attempt, ct);
                    return;
                case ProjectionScopeStatusRoutePhase.Blocked:
                    await CompleteCutoverAsync(route, ct);
                    return;
                default:
                    await MaintainLegacyWriterAsync(route, attempt, ct);
                    return;
            }
        }

        await MaintainLegacyWriterAsync(route: null, attempt, ct);
    }

    // ── legacy writer (no route, or rolled-back legacy route) ────────────────────────────────

    /// <summary>
    /// The legacy shadow is the writer. The scope ensures it exists on its own turn (it owns the
    /// decision; no activation service decides from relay evidence) and, with a fresh terminal
    /// admission, starts warming the terminal materializer at the next route epoch.
    /// </summary>
    private async Task MaintainLegacyWriterAsync(ProjectionScopeStatusRoute? route, int attempt, CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime == null || dispatchPort == null)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy: actor runtime ports unavailable. actorId={ActorId}",
                Id);
            return;
        }

        await EnsureLegacyStatusShadowAsync(runtime, dispatchPort, ct);

        var admission = await ReadTerminalAdmissionAsync(ct);
        if (admission.Readers == null)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy: fleet admission readers unavailable. actorId={ActorId}",
                Id);
            return;
        }

        if (admission.Grant == null)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy: no fresh terminal admission. actorId={ActorId} attempt={Attempt}",
                Id,
                attempt);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }

        await StartWarmingAsync(
            ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
                (route?.RouteEpoch ?? 0) + 1,
                ProjectionScopeStatusRoutePhase.Warming),
            admission.Grant,
            runtime,
            dispatchPort,
            ct);
    }

    // ── active terminal writer ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The terminal materializer is the writer (ACTIVE, or a phase-less route of a binary that
    /// adopted without phases). Heal the derived facts (relay, materializer existence, pending
    /// release of the previous writer), reconcile a legacy relay that reappeared after the
    /// release, and roll back to the legacy writer if the fleet explicitly revoked the gate.
    /// </summary>
    private async Task MaintainActiveTerminalRouteAsync(ProjectionScopeStatusRoute route, CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(Id);
        await UpsertTerminalStatusRelayAsync(terminalActorId, ct);
        if (runtime != null && !await runtime.ExistsAsync(terminalActorId))
            _ = await runtime.CreateByKindAsync(ProjectionScopeStatusGAgent.AgentKind, terminalActorId, ct);

        await ReleasePreviousWriterIfPendingAsync(route, ct);
        await ReconcileReappearedLegacyStatusRelayAsync(route, ct);

        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime == null || dispatchPort == null)
            return;

        var admission = await ReadTerminalAdmissionAsync(ct);
        if (admission.Revoked)
        {
            _logger.LogWarning(
                "Projection scope status route: terminal admission revoked; rolling back to the legacy writer. actorId={ActorId} routeEpoch={RouteEpoch}",
                Id,
                route.RouteEpoch);
            await StartWarmingAsync(
                ProjectionScopeStatusRoutePolicy.BuildLegacyRoute(
                    route.RouteEpoch + 1,
                    ProjectionScopeStatusRoutePhase.Warming),
                grant: null,
                runtime,
                dispatchPort,
                ct);
        }
    }

    // ── cutover phases ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 1: install the new writer on this scope's stream (relay + actor), then commit the
    /// warming route at the next epoch — the warming version is the version of that commit and
    /// its publication is the first routed envelope the new writer observes and reports. The
    /// current writer is not touched: it keeps writing until the route is blocked.
    /// </summary>
    private async Task StartWarmingAsync(
        ProjectionScopeStatusRoute route,
        RuntimeFleetCapabilityAdmissionGrant? grant,
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        CancellationToken ct)
    {
        var now = grant?.ValidatedAt ?? Now();
        route.WarmStartedVersion = CurrentScopeVersion + 1;
        route.ActivatedAtUtc = Timestamp.FromDateTimeOffset(now);
        if (grant != null)
        {
            var admission = grant.Admission;
            route.ActivationProof = new ProjectionMaterializationActivationProof
            {
                AuthorityStateVersion = admission.AuthorityStateVersion,
                CapabilityEpoch = admission.CapabilityEpoch,
                MembershipEpoch = admission.MembershipEpoch,
                MembershipDigest = admission.MembershipDigest,
                DeploymentRevision = admission.DeploymentRevision,
                ValidatedAtUtc = Timestamp.FromDateTimeOffset(grant.ValidatedAt),
                ValidUntilUtc = admission.MembershipValidUntil?.Clone(),
            };
        }

        // The new writer is installed before the warming fact is committed: relays forward at
        // publication time (no replay), so the publication of the warming commit itself is the
        // first routed envelope the new writer observes and reports — no further source event
        // is needed for an idle source to catch up. A restart in between leaves a relay whose
        // publications carry no route yet; the writer ignores them and the next activation
        // re-runs this decision.
        await InstallWarmingWriterAsync(route, runtime, dispatchPort, ct);
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteWarmingStartedEvent
        {
            Route = route,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(now),
        });
        _logger.LogInformation(
            "Projection scope status route warming started. actorId={ActorId} contractId={ContractId} routeEpoch={RouteEpoch} warmStartedVersion={WarmStartedVersion}",
            Id,
            route.ContractId,
            route.RouteEpoch,
            route.WarmStartedVersion);

        // Liveness without waiting for another activation: if the caught-up report is lost the
        // durable continuation re-probes (backed off) until the cutover completes.
        await ScheduleStatusRouteAdoptionRetryAsync(attempt: 0, ct);
    }

    /// <summary>
    /// Phase 1 continued (activation while WARMING): re-install the warming writer and, when it
    /// has reported a caught-up version, proceed; otherwise commit a probe so its publication
    /// carries the warming route through the relay even for an otherwise idle source.
    /// </summary>
    private async Task ContinueWarmingAsync(ProjectionScopeStatusRoute route, int attempt, CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime == null || dispatchPort == null)
        {
            _logger.LogInformation(
                "Projection scope status route warming cannot continue: actor runtime ports unavailable. actorId={ActorId} routeEpoch={RouteEpoch}",
                Id,
                route.RouteEpoch);
            return;
        }

        await InstallWarmingWriterAsync(route, runtime, dispatchPort, ct);
        if (route.CaughtUpVersion >= route.WarmStartedVersion)
        {
            await BlockAndCompleteCutoverAsync(route, ct);
            return;
        }

        // The committed probe publishes the warming route through the (re)installed relay so
        // the writer can report even when the source is otherwise idle; the continuation
        // repeats, backed off, until the writer catches up.
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteWarmingProbedEvent
        {
            RouteEpoch = route.RouteEpoch,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
        await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
    }

    private async Task InstallWarmingWriterAsync(
        ProjectionScopeStatusRoute route,
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        CancellationToken ct)
    {
        if (ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route))
        {
            var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(Id);
            await UpsertTerminalStatusRelayAsync(terminalActorId, ct);
            await EnsureTerminalStatusMaterializerAsync(runtime, dispatchPort, terminalActorId, ct);
            return;
        }

        await EnsureLegacyStatusShadowAsync(runtime, dispatchPort, ct);
    }

    /// <summary>
    /// Phase 2: the warming writer reported an observed version. A report at or above the
    /// warming version means the writer has seen everything since warming started, so the
    /// route is blocked, the previous writer is released and the route is flipped — each a
    /// committed fact of this turn.
    /// </summary>
    [EventHandler]
    public async Task HandleStatusWriterCaughtUpAsync(ProjectionScopeStatusWriterCaughtUpEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!OwnsStatusRoute)
            return;

        var route = State.StatusRoute;
        if (route == null ||
            route.Phase != ProjectionScopeStatusRoutePhase.Warming ||
            route.RouteEpoch != evt.RouteEpoch ||
            !string.Equals(evt.SourceScopeActorId, Id, StringComparison.Ordinal))
        {
            return;
        }

        if (evt.ObservedVersion > route.CaughtUpVersion)
        {
            await PersistDomainEventAsync(new ProjectionScopeStatusRouteCaughtUpEvent
            {
                RouteEpoch = route.RouteEpoch,
                ObservedVersion = evt.ObservedVersion,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
            });
        }

        route = State.StatusRoute!;
        if (route.CaughtUpVersion >= route.WarmStartedVersion)
            await BlockAndCompleteCutoverAsync(route, ct: CancellationToken.None);
    }

    /// <summary>Phase 3: block the route (this scope consumes no observation until the flip).</summary>
    private async Task BlockAndCompleteCutoverAsync(ProjectionScopeStatusRoute route, CancellationToken ct)
    {
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteBlockedEvent
        {
            RouteEpoch = route.RouteEpoch,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
        await CompleteCutoverAsync(State.StatusRoute!, ct);
    }

    /// <summary>
    /// Phases 4 and 5 (also resumed on activation while BLOCKED): release the previous writer
    /// — its queued work is at or below the blocked version, so the new writer's first write at
    /// a higher epoch is the same-version takeover — and flip the route to ACTIVE.
    /// </summary>
    private async Task CompleteCutoverAsync(ProjectionScopeStatusRoute route, CancellationToken ct)
    {
        await ReleasePreviousWriterIfPendingAsync(route, ct);

        var flipped = route.Clone();
        flipped.Phase = ProjectionScopeStatusRoutePhase.Active;
        flipped.FlipVersion = CurrentScopeVersion + 1;
        flipped.LegacyRouteReleased = true;
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = flipped,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
        _logger.LogInformation(
            "Projection scope status route flipped. actorId={ActorId} contractId={ContractId} routeEpoch={RouteEpoch} flipVersion={FlipVersion}",
            Id,
            flipped.ContractId,
            flipped.RouteEpoch,
            flipped.FlipVersion);
    }

    /// <summary>
    /// Removes the previous writer's relay from this scope's stream and releases its actor:
    /// the legacy shadow when the route selects the terminal materializer, the terminal
    /// materializer when the route rolled back to the legacy shadow. Recorded per epoch.
    /// </summary>
    private async Task ReleasePreviousWriterIfPendingAsync(ProjectionScopeStatusRoute route, CancellationToken ct)
    {
        if (route.LegacyRouteReleased)
            return;

        if (ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route))
            await RemoveLegacyStatusRelayAndReleaseShadowAsync(ProjectionScopeStatusRoutes.BuildLegacyActorId(Id), ct);
        else
            await RemoveTerminalStatusRelayAndReleaseMaterializerAsync(ct);

        await PersistDomainEventAsync(new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = route.RouteEpoch,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
    }

    // ── reconciliation on this scope's own observations ──────────────────────────────────────

    /// <summary>
    /// Runs on every observed envelope of this scope, before the observation is dispatched.
    /// For a source scope: a BLOCKED route refuses the observation (retryable) so nothing is
    /// published until the flip. For the legacy status shadow: an observed source state whose
    /// route selects the terminal writer in a writing phase supersedes it (it releases itself;
    /// the source's next activation also reconciles its relay), and a legacy route that is
    /// warming (rollback) is reported caught-up to the source.
    /// </summary>
    private async Task<bool> ReconcileStatusRouteOnObservationAsync(EventEnvelope envelope)
    {
        if (RuntimeMode != ProjectionRuntimeMode.DurableMaterialization)
            return false;

        if (!string.Equals(
                State.ProjectionKind,
                ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var sourceState) ||
            sourceState == null)
        {
            return false;
        }

        var route = sourceState.StatusRoute;
        if (ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(route))
        {
            _logger.LogInformation(
                "Legacy status shadow observed a source whose status route selects the terminal materializer; releasing itself. actorId={ActorId} routeEpoch={RouteEpoch}",
                Id,
                route!.RouteEpoch);
            await HandleReleaseAsync(new ReleaseProjectionScopeCommand
            {
                RootActorId = State.RootActorId,
                ProjectionKind = State.ProjectionKind,
                SessionId = State.SessionId,
                Mode = State.Mode,
            });
            return true;
        }

        if (ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route) &&
            route!.Phase == ProjectionScopeStatusRoutePhase.Warming &&
            stateEvent != null)
        {
            await SendToAsync(State.RootActorId, new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = State.RootActorId,
                RouteEpoch = route.RouteEpoch,
                ObservedVersion = stateEvent.Version,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now()),
            });
        }

        return false;
    }

    /// <summary>
    /// The legacy shadow may reappear after its release (a delayed relay upsert, or an older
    /// activation service that still ensured it). The committed route already excludes it, so
    /// every activation reconciles the authoritative relay evidence: a reappeared legacy relay is
    /// removed and the shadow is released again without a new route decision.
    /// </summary>
    private async Task ReconcileReappearedLegacyStatusRelayAsync(ProjectionScopeStatusRoute route, CancellationToken ct)
    {
        if (!route.LegacyRouteReleased)
            return;

        var authority = Services.GetService<IStreamForwardingBindingAuthority>();
        if (authority == null)
            return;

        var legacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(Id);
        var binding = await authority.GetAsync(Id, legacyActorId, ct);
        if (binding == null)
            return;

        _logger.LogWarning(
            "Projection scope legacy status relay reappeared after the terminal route was adopted; removing it and releasing the shadow again. actorId={ActorId} routeEpoch={RouteEpoch}",
            Id,
            route.RouteEpoch);
        await RemoveLegacyStatusRelayAndReleaseShadowAsync(legacyActorId, ct);
    }

    // ── durable adoption retry ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A durable scope that activates before the terminal fleet gate opens would otherwise stay
    /// on the legacy writer until its next activation, which for an always-active scope never
    /// comes. The retry is a backed-off self continuation through the durable callback
    /// scheduler; the attempt count travels in the command, never in actor memory, and the last
    /// delay repeats for as long as the gate stays closed.
    /// </summary>
    private async Task ScheduleStatusRouteAdoptionRetryAsync(int attempt, CancellationToken ct)
    {
        if (Services.GetService<IActorRuntimeCallbackScheduler>() == null)
            return;

        var nextAttempt = attempt + 1;
        _ = await ScheduleSelfDurableTimeoutAsync(
            StatusRouteAdoptionRetryCallbackId,
            StatusRouteAdoptionRetryDelays[Math.Min(attempt, StatusRouteAdoptionRetryDelays.Length - 1)],
            new RetryProjectionScopeStatusRouteAdoptionCommand { Attempt = nextAttempt },
            ct: ct);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleRetryStatusRouteAdoptionAsync(RetryProjectionScopeStatusRouteAdoptionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AdvanceStatusRouteAsync(CancellationToken.None, command.Attempt);
    }

    // ── writer actors and relays ─────────────────────────────────────────────────────────────

    private Task UpsertTerminalStatusRelayAsync(string terminalActorId, CancellationToken ct) =>
        Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(Id)
            .UpsertRelayAsync(
                ProjectionScopeObservationRelayBinding.Create(
                    Id,
                    terminalActorId,
                    ProjectionScopeStatusGAgent.AgentKind,
                    activationGeneration: 1),
                ct);

    private async Task EnsureTerminalStatusMaterializerAsync(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        string terminalActorId,
        CancellationToken ct)
    {
        if (!await runtime.ExistsAsync(terminalActorId))
            _ = await runtime.CreateByKindAsync(ProjectionScopeStatusGAgent.AgentKind, terminalActorId, ct);

        await DispatchLifecycleAsync(
            dispatchPort,
            terminalActorId,
            new EnsureProjectionScopeCommand
            {
                RootActorId = Id,
                ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            },
            ct);
    }

    /// <summary>
    /// The legacy event-sourced status shadow of this scope is ensured by this scope itself, on
    /// its own turn (create by kind if missing, then the lifecycle command into its inbox; the
    /// shadow writes its own relay on this scope's stream). No activation service decides this
    /// from relay evidence any more, which removes the race between a warm activation-service
    /// return and an adoption in flight.
    /// </summary>
    private async Task EnsureLegacyStatusShadowAsync(
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        CancellationToken ct)
    {
        // Hosts without the status runtime registered (no legacy shadow kind) have no status
        // mirror at all; nothing to ensure.
        var registry = Services.GetService<IAgentKindRegistry>();
        if (registry == null ||
            !registry.TryGetKindForAgentType(
                typeof(ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>),
                out var legacyKind))
        {
            return;
        }

        var legacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(Id);
        if (!await runtime.ExistsAsync(legacyActorId))
            _ = await runtime.CreateByKindAsync(legacyKind, legacyActorId, ct);

        await DispatchLifecycleAsync(
            dispatchPort,
            legacyActorId,
            new EnsureProjectionScopeCommand
            {
                RootActorId = Id,
                ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            },
            ct);
    }

    private async Task RemoveLegacyStatusRelayAndReleaseShadowAsync(string legacyActorId, CancellationToken ct)
    {
        await Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(Id)
            .RemoveRelayAsync(legacyActorId, ct);

        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime != null && dispatchPort != null && await runtime.ExistsAsync(legacyActorId))
        {
            await DispatchLifecycleAsync(
                dispatchPort,
                legacyActorId,
                new ReleaseProjectionScopeCommand
                {
                    RootActorId = Id,
                    ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                    Mode = ProjectionScopeMode.DurableMaterialization,
                },
                ct);
        }
    }

    private async Task RemoveTerminalStatusRelayAndReleaseMaterializerAsync(CancellationToken ct)
    {
        var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(Id);
        await Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(Id)
            .RemoveRelayAsync(terminalActorId, ct);

        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime != null && dispatchPort != null && await runtime.ExistsAsync(terminalActorId))
        {
            await DispatchLifecycleAsync(
                dispatchPort,
                terminalActorId,
                new ReleaseProjectionScopeCommand
                {
                    RootActorId = Id,
                    ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                    Mode = ProjectionScopeMode.DurableMaterialization,
                },
                ct);
        }
    }

    private async Task DispatchLifecycleAsync<TCommand>(
        IActorDispatchPort dispatchPort,
        string targetActorId,
        TCommand command,
        CancellationToken ct)
        where TCommand : Google.Protobuf.IMessage
    {
        var envelope = ProjectionScopeCommandEnvelopeFactory.Create(command, targetActorId);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(Id, targetActorId);
        _ = await dispatchPort.DispatchAsync(targetActorId, envelope, ct);
    }

    // ── fleet admission ──────────────────────────────────────────────────────────────────────

    private readonly record struct TerminalAdmissionRead(
        object? Readers,
        RuntimeFleetCapabilityAdmissionGrant? Grant,
        bool Revoked);

    private async Task<TerminalAdmissionRead> ReadTerminalAdmissionAsync(CancellationToken ct)
    {
        var admissionReader = Services.GetService<IRuntimeFleetCapabilityAdmissionReader>();
        var membershipReader = Services.GetService<IRuntimeLocalMembershipIdentityReader>();
        if (admissionReader == null || membershipReader == null)
            return new TerminalAdmissionRead(null, null, false);

        var grant = await RuntimeFleetCapabilityAdmissionValidation.GetGrantedAdmissionAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
            ProjectionScopeStatusGAgent.ContractId,
            (int)ProjectionScopeStatusGAgent.ContractVersion,
            admissionReader,
            membershipReader,
            Services.GetService<TimeProvider>(),
            Services.GetService<RuntimeActorStateMigrationAdmissionOptions>(),
            ct);
        if (grant != null)
            return new TerminalAdmissionRead(admissionReader, grant, false);

        // Only an explicit revocation rolls an active terminal route back; absence or expiry of
        // the admission is not evidence that the terminal writer must stop.
        RuntimeFleetCapabilityAdmission? admission;
        try
        {
            admission = await admissionReader.GetAsync(RuntimeFleetCapability.ProjectionScopeStatusTerminalV1, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            admission = null;
        }

        var revoked = admission != null &&
                      admission.Status == RuntimeFleetCapabilityGateStatus.Revoked &&
                      string.Equals(admission.ContractId, ProjectionScopeStatusGAgent.ContractId, StringComparison.Ordinal);
        return new TerminalAdmissionRead(admissionReader, null, revoked);
    }

    private DateTimeOffset Now() =>
        (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
}

/// <summary>
/// The source scope's status route is BLOCKED (cutover in flight): the observation is refused
/// so the source publishes nothing until the route is flipped; the envelope is redelivered.
/// </summary>
public sealed class ProjectionScopeStatusRouteBlockedException(string scopeActorId, long routeEpoch)
    : InvalidOperationException(
        $"Projection scope '{scopeActorId}' status route epoch {routeEpoch} is blocked for cutover; the observation is retried after the flip.")
{
    public string ScopeActorId { get; } = scopeActorId;

    public long RouteEpoch { get; } = routeEpoch;
}
