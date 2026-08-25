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
/// <item>BLOCKED — the scope stops consuming observations; a committed drain probe is published
/// only after the Phase-A bridge receipt, and the candidate writer may perform the epoch-fenced
/// same-version takeover while the previous writer drains that exact version.</item>
/// <item>previous writer released — the release is dispatched to it and the route stays
/// BLOCKED until the previous writer confirms, with a typed continuation
/// (<see cref="ProjectionScopeStatusWriterReleasedEvent"/>), that its release is committed;
/// inbox acceptance of the release command is never taken as the release.</item>
/// <item>ACTIVE — the route is flipped only after the exact previous writer confirms its durable
/// drain; the selected writer then handles every later terminal outcome.</item>
/// </list>
/// Phase A contributes only the historical typed quiescence receipt. Phase B may start or resume
/// a route only while its separate fleet admission is fresh and after the source, legacy writer,
/// and terminal writer have each returned a runtime-owned schema-adoption seal. Persisted
/// WARMING/BLOCKED routes stay frozen until those proofs are bound. ACTIVE and phase-less routes
/// keep their existing writer while the same three seals are attached in the background.
/// </summary>
public abstract partial class ProjectionScopeGAgentBase<TContext>
{
    internal const string StatusRouteAdoptionRetryCallbackId = "projection-scope-status-route-adoption";

    /// <summary>
    /// Backed-off retry schedule for observing bridge quiescence and repairing a persisted
    /// WARMING/BLOCKED route, or binding seals to an existing ACTIVE route. The last delay repeats
    /// while the receipt is unavailable or repair dependencies are not ready.
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
        if (route?.Phase is ProjectionScopeStatusRoutePhase.Warming or
            ProjectionScopeStatusRoutePhase.Blocked)
        {
            var quiescence = await ReadTerminalQuiescenceAsync(ct);
            if (quiescence.Receipt != null)
            {
                if (!await EnsurePhaseBRouteSealsAsync(route, attempt, ct))
                    return;

                route = State.StatusRoute!;
                if (route.Phase == ProjectionScopeStatusRoutePhase.Warming)
                    await ContinueWarmingAsync(route, attempt, ct);
                else
                    await CompleteCutoverAsync(route, attempt, ct);
                return;
            }

            _logger.LogInformation(
                "Projection scope status route freezes its persisted cutover until the Phase-A bridge is quiesced. actorId={ActorId} contractId={ContractId} routeEpoch={RouteEpoch} phase={Phase} attempt={Attempt}",
                Id,
                route.ContractId,
                route.RouteEpoch,
                route.Phase,
                attempt);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }

        if (ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route))
        {
            await MaintainActiveTerminalRouteAsync(route!, attempt, ct);
            return;
        }

        if (ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route))
        {
            await MaintainLegacyWriterAsync(route!, attempt, ct);
            return;
        }

        await MaintainLegacyWriterAsync(route: null, attempt, ct);
    }

    // ── legacy writer (no route, or rolled-back legacy route) ────────────────────────────────

    /// <summary>
    /// The legacy shadow is the writer. The scope ensures it exists on its own turn (it owns the
    /// decision; no activation service decides from relay evidence). Phase A never starts a new
    /// terminal cutover: mixed fleets lack the drain proof, while a fully upgraded fleet closes
    /// V2 as QUIESCED for the later Phase-B artifact.
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
        if (route?.LegacyRouteReleased == true)
            await ReconcileReleasedPreviousWriterRelayAsync(route, ct);

        var quiescence = await ReadTerminalQuiescenceAsync(ct);
        if (!quiescence.ReaderAvailable)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy: fleet admission readers unavailable. actorId={ActorId}",
                Id);
            return;
        }

        if (quiescence.Receipt == null)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy until the Phase-A drain bridge is quiesced. actorId={ActorId} attempt={Attempt}",
                Id,
                attempt);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }

        var admission = await ReadActivationSealAdmissionAsync(ct);
        if (admission == null)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy until the fresh Phase-B activation seal is open. actorId={ActorId} attempt={Attempt}",
                Id,
                attempt);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }

        await EnsureStatusRoutePreparationAsync(
            ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
                (route?.RouteEpoch ?? 0) + 1,
                ProjectionScopeStatusRoutePhase.Warming),
            resumesPersistedRoute: false,
            admission,
            runtime,
            dispatchPort,
            attempt,
            ct);
    }

    // ── active terminal writer ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The terminal materializer is the writer (ACTIVE, or a phase-less route of an earlier
    /// binary). Heal its relay/materializer and remove a previous-writer relay that reappeared
    /// after a committed release. Phase A does not reconstruct an unconfirmed ACTIVE release,
    /// upgrade an earlier terminal contract, or roll the route back.
    /// </summary>
    private async Task MaintainActiveTerminalRouteAsync(ProjectionScopeStatusRoute route, int attempt, CancellationToken ct)
    {
        if (!StatusRouteHasAllRequiredSeals(route))
            _ = await EnsurePhaseBRouteSealsAsync(route, attempt, ct);

        route = State.StatusRoute ?? route;
        var runtime = Services.GetService<IActorRuntime>();
        var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(Id);
        await UpsertTerminalStatusRelayAsync(terminalActorId, ct);
        if (runtime != null && !await runtime.ExistsAsync(terminalActorId))
            _ = await runtime.CreateByKindAsync(ProjectionScopeStatusGAgent.AgentKind, terminalActorId, ct);

        // Phase A repairs only a relay that contradicts an already committed release. It does not
        // infer or reconstruct release proof for an ACTIVE route whose flag is still false.
        route = State.StatusRoute ?? route;
        await ReconcileReleasedPreviousWriterRelayAsync(route, ct);
    }

    // ── Phase-B activation seals ─────────────────────────────────────────────────────────────

    private async Task<bool> EnsurePhaseBRouteSealsAsync(
        ProjectionScopeStatusRoute route,
        int attempt,
        CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        var admission = await ReadActivationSealAdmissionAsync(ct);
        if (admission == null)
        {
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return false;
        }

        if (StatusRouteHasAllRequiredSeals(route))
            return true;

        if (runtime == null || dispatchPort == null)
        {
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return false;
        }

        return await EnsureStatusRoutePreparationAsync(
            route.Clone(),
            resumesPersistedRoute: true,
            admission,
            runtime,
            dispatchPort,
            attempt,
            ct);
    }

    private async Task<bool> EnsureStatusRoutePreparationAsync(
        ProjectionScopeStatusRoute candidateRoute,
        bool resumesPersistedRoute,
        RuntimeFleetCapabilityAdmissionGrant admission,
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        int attempt,
        CancellationToken ct)
    {
        var registry = Services.GetService<IAgentKindRegistry>();
        if (registry == null ||
            !registry.TryGetKindForAgentType(
                typeof(ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>),
                out var legacyKind) ||
            !TryCreateSourceActivationSeal(out var sourceSeal))
        {
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return false;
        }

        var preparation = State.StatusRoutePreparation;
        if (!MatchesPreparation(preparation, candidateRoute, resumesPersistedRoute))
        {
            preparation = new ProjectionScopeStatusRoutePreparation
            {
                CandidateRoute = candidateRoute.Clone(),
                SourceScopeActorId = Id,
                SourceAgentKind = sourceSeal.AgentKind,
                LegacyWriterActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(Id),
                LegacyWriterAgentKind = legacyKind,
                TerminalWriterActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(Id),
                TerminalWriterAgentKind = ProjectionScopeStatusGAgent.AgentKind,
                ResumesPersistedRoute = resumesPersistedRoute,
                PreparedAtUtc = Timestamp.FromDateTimeOffset(admission.ValidatedAt),
            };
            preparation.ActivationSeals.Add(sourceSeal);
            await PersistDomainEventAsync(new ProjectionScopeStatusRoutePreparationStartedEvent
            {
                Preparation = preparation,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(admission.ValidatedAt),
            });
            preparation = State.StatusRoutePreparation!;
        }

        return await ContinueStatusRoutePreparationAsync(
            preparation,
            runtime,
            dispatchPort,
            attempt,
            ct);
    }

    private async Task<bool> ContinueStatusRoutePreparationAsync(
        ProjectionScopeStatusRoutePreparation preparation,
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        int attempt,
        CancellationToken ct)
    {
        if (!ProjectionScopeStatusActivationSealPolicy.HasAllRequiredSeals(
                preparation.ActivationSeals,
                preparation) ||
            !TryCreateSourceActivationSeal(out var currentSourceSeal) ||
            !Equals(
                ProjectionScopeStatusActivationSealPolicy.Find(
                    preparation.ActivationSeals,
                    ProjectionScopeStatusActorRole.Source),
                currentSourceSeal))
        {
            await RequestMissingStatusActorSealsAsync(preparation, runtime, dispatchPort, ct);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return false;
        }

        var admission = await ReadActivationSealAdmissionAsync(ct);
        var quiescence = await ReadTerminalQuiescenceAsync(ct);
        if (admission == null || quiescence.Receipt == null)
        {
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return false;
        }

        if (preparation.ResumesPersistedRoute)
        {
            await PersistDomainEventAsync(new ProjectionScopeStatusRouteActivationSealsBoundEvent
            {
                RouteEpoch = preparation.CandidateRoute.RouteEpoch,
                ActivationSeals = { preparation.ActivationSeals.Select(static seal => seal.Clone()) },
                OccurredAtUtc = Timestamp.FromDateTimeOffset(admission.ValidatedAt),
            });
            return true;
        }

        var route = preparation.CandidateRoute.Clone();
        route.ActivationSeals.Clear();
        route.ActivationSeals.Add(preparation.ActivationSeals.Select(static seal => seal.Clone()));
        await StartWarmingAsync(route, admission, runtime, dispatchPort, ct);
        return true;
    }

    private async Task RequestMissingStatusActorSealsAsync(
        ProjectionScopeStatusRoutePreparation preparation,
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        CancellationToken ct)
    {
        var requests = new[]
        {
            new
            {
                Role = ProjectionScopeStatusActorRole.LegacyWriter,
                ActorId = preparation.LegacyWriterActorId,
                AgentKind = preparation.LegacyWriterAgentKind,
            },
            new
            {
                Role = ProjectionScopeStatusActorRole.TerminalWriter,
                ActorId = preparation.TerminalWriterActorId,
                AgentKind = preparation.TerminalWriterAgentKind,
            },
        };
        foreach (var request in requests)
        {
            if (ProjectionScopeStatusActivationSealPolicy.Find(
                    preparation.ActivationSeals,
                    request.Role) != null)
            {
                continue;
            }

            if (!await runtime.ExistsAsync(request.ActorId))
                _ = await runtime.CreateByKindAsync(request.AgentKind, request.ActorId, ct);

            await SendToAsync(
                request.ActorId,
                new RequestProjectionScopeStatusActorSealCommand
                {
                    SourceScopeActorId = Id,
                    RouteEpoch = preparation.CandidateRoute.RouteEpoch,
                    Role = request.Role,
                    ExpectedActorId = request.ActorId,
                    ExpectedAgentKind = request.AgentKind,
                },
                ct);
        }
    }

    [EventHandler]
    public async Task HandleStatusActorSealReadyAsync(ProjectionScopeStatusActorSealReadyEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var preparation = State.StatusRoutePreparation;
        var seal = evt.Seal;
        if (!OwnsStatusRoute ||
            preparation?.CandidateRoute == null ||
            seal == null ||
            preparation.CandidateRoute.RouteEpoch != evt.RouteEpoch ||
            !string.Equals(evt.SourceScopeActorId, Id, StringComparison.Ordinal) ||
            !ProjectionScopeStatusActivationSealPolicy.IsExpectedWriterSeal(preparation, seal) ||
            !IsExpectedDirectPublisher(seal.ActorId))
        {
            return;
        }

        var existing = ProjectionScopeStatusActivationSealPolicy.Find(
            preparation.ActivationSeals,
            seal.Role);
        if (existing == null || !existing.Equals(seal))
        {
            await PersistDomainEventAsync(new ProjectionScopeStatusActorSealRecordedEvent
            {
                RouteEpoch = evt.RouteEpoch,
                Seal = seal.Clone(),
                OccurredAtUtc = evt.OccurredAtUtc?.Clone() ?? Timestamp.FromDateTimeOffset(Now()),
            });
        }

        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime == null || dispatchPort == null)
            return;

        if (await ContinueStatusRoutePreparationAsync(
                State.StatusRoutePreparation!,
                runtime,
                dispatchPort,
                attempt: 0,
                CancellationToken.None) &&
            State.StatusRoute?.Phase is ProjectionScopeStatusRoutePhase.Warming or
                ProjectionScopeStatusRoutePhase.Blocked)
        {
            await AdvanceStatusRouteAsync(CancellationToken.None);
        }
    }

    [EventHandler]
    public async Task HandleStatusActorSealRequestAsync(RequestProjectionScopeStatusActorSealCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Role != ProjectionScopeStatusActorRole.LegacyWriter ||
            command.RouteEpoch <= 0 ||
            string.IsNullOrWhiteSpace(command.SourceScopeActorId) ||
            !string.Equals(command.ExpectedActorId, Id, StringComparison.Ordinal) ||
            !IsExpectedDirectPublisher(command.SourceScopeActorId) ||
            (!string.IsNullOrWhiteSpace(State.RootActorId) &&
             !string.Equals(State.RootActorId, command.SourceScopeActorId, StringComparison.Ordinal)) ||
            !ProjectionScopeStatusActivationSealPolicy.TryCreate(
                Services.GetService<IRuntimeActorStateSchemaContextReader>(),
                ProjectionScopeStatusActorRole.LegacyWriter,
                Id,
                command.ExpectedAgentKind,
                out var seal))
        {
            return;
        }

        await SendToAsync(command.SourceScopeActorId, new ProjectionScopeStatusActorSealReadyEvent
        {
            SourceScopeActorId = command.SourceScopeActorId,
            RouteEpoch = command.RouteEpoch,
            Seal = seal,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
    }

    private bool TryCreateSourceActivationSeal(out ProjectionScopeStatusActorSeal seal)
    {
        var reader = Services.GetService<IRuntimeActorStateSchemaContextReader>();
        var agentKind = reader?.Current?.AgentKind ?? string.Empty;
        return ProjectionScopeStatusActivationSealPolicy.TryCreate(
            reader,
            ProjectionScopeStatusActorRole.Source,
            Id,
            agentKind,
            out seal);
    }

    private bool HasLegacyWriterBoundPhaseBSeals(
        ProjectionScopeStatusRoute route,
        string sourceScopeActorId)
    {
        var reader = Services.GetService<IRuntimeActorStateSchemaContextReader>();
        var agentKind = reader?.Current?.AgentKind ?? string.Empty;
        return ProjectionScopeStatusActivationSealPolicy.TryCreate(
                   reader,
                   ProjectionScopeStatusActorRole.LegacyWriter,
                   Id,
                   agentKind,
                   out var currentWriterSeal) &&
               ProjectionScopeStatusActivationSealPolicy.RouteHasAllRequiredWriterSeals(
                   route,
                   sourceScopeActorId,
                   ProjectionScopeStatusRoutes.BuildLegacyActorId(sourceScopeActorId),
                   ProjectionScopeStatusRoutes.BuildTerminalActorId(sourceScopeActorId),
                   currentWriterSeal);
    }

    private async Task<bool> HasLegacyWriterLivePhaseBProofsAsync() =>
        (await ReadTerminalQuiescenceAsync(CancellationToken.None)).Receipt != null &&
        await ReadActivationSealAdmissionAsync(CancellationToken.None) != null;

    private static bool MatchesPreparation(
        ProjectionScopeStatusRoutePreparation? preparation,
        ProjectionScopeStatusRoute candidateRoute,
        bool resumesPersistedRoute) =>
        preparation?.CandidateRoute != null &&
        preparation.CandidateRoute.RouteEpoch == candidateRoute.RouteEpoch &&
        string.Equals(
            preparation.CandidateRoute.ContractId,
            candidateRoute.ContractId,
            StringComparison.Ordinal) &&
        preparation.CandidateRoute.ContractVersion == candidateRoute.ContractVersion &&
        preparation.CandidateRoute.Phase == candidateRoute.Phase &&
        preparation.ResumesPersistedRoute == resumesPersistedRoute;

    private bool StatusRouteHasAllRequiredSeals(ProjectionScopeStatusRoute route)
    {
        if (!TryCreateSourceActivationSeal(out var currentSourceSeal))
            return false;

        var registry = Services.GetService<IAgentKindRegistry>();
        if (registry == null ||
            !registry.TryGetKindForAgentType(
                typeof(ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>),
                out var legacyKind) ||
            !ProjectionScopeStatusActivationSealPolicy.RouteHasAllRequiredSeals(
                route,
                Id,
                currentSourceSeal.AgentKind,
                ProjectionScopeStatusRoutes.BuildLegacyActorId(Id),
                legacyKind,
                ProjectionScopeStatusRoutes.BuildTerminalActorId(Id),
                ProjectionScopeStatusGAgent.AgentKind))
        {
            return false;
        }

        return Equals(
            ProjectionScopeStatusActivationSealPolicy.Find(
                route.ActivationSeals,
                ProjectionScopeStatusActorRole.Source),
            currentSourceSeal);
    }

    private async Task StartWarmingAsync(
        ProjectionScopeStatusRoute route,
        RuntimeFleetCapabilityAdmissionGrant admission,
        IActorRuntime runtime,
        IActorDispatchPort dispatchPort,
        CancellationToken ct)
    {
        route.WarmStartedVersion = CurrentScopeVersion + 1;
        route.ActivatedAtUtc = Timestamp.FromDateTimeOffset(admission.ValidatedAt);
        route.ActivationProof = BuildActivationProof(admission);
        if (!await InstallWarmingWriterAsync(route, runtime, dispatchPort, ct))
            return;

        await PersistDomainEventAsync(new ProjectionScopeStatusRouteWarmingStartedEvent
        {
            Route = route,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(admission.ValidatedAt),
        });
        await ScheduleStatusRouteAdoptionRetryAsync(attempt: 0, ct);
    }

    private static ProjectionMaterializationActivationProof BuildActivationProof(
        RuntimeFleetCapabilityAdmissionGrant grant)
    {
        var admission = grant.Admission;
        return new ProjectionMaterializationActivationProof
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

    // ── receipt-gated repair of persisted cutovers ────────────────────────────────────────────

    /// <summary>
    /// Reinstalls the candidate writer for an already committed WARMING route and publishes a
    /// fresh probe. Persisted caught-up state predates the bridge receipt and is never trusted to
    /// advance the route; only a new authenticated continuation can block it.
    /// </summary>
    private async Task ContinueWarmingAsync(ProjectionScopeStatusRoute route, int attempt, CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime == null || dispatchPort == null)
        {
            _logger.LogInformation(
                "Projection scope status route warming cannot resume: actor runtime ports unavailable. actorId={ActorId} routeEpoch={RouteEpoch}",
                Id,
                route.RouteEpoch);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }

        if (!await InstallWarmingWriterAsync(route, runtime, dispatchPort, ct))
        {
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }
        var requiredObservedVersion = route.WarmingProbeVersion > 0
            ? route.WarmingProbeVersion
            : CurrentScopeVersion + 1;
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteWarmingProbedEvent
        {
            RouteEpoch = route.RouteEpoch,
            RequiredObservedVersion = requiredObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
        await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
    }

    private async Task<bool> InstallWarmingWriterAsync(
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
            return true;
        }

        var registry = Services.GetService<IAgentKindRegistry>();
        if (registry == null ||
            !registry.TryGetKindForAgentType(
                typeof(ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>),
                out var legacyKind))
        {
            return false;
        }

        await EnsureLegacyStatusShadowAsync(runtime, dispatchPort, ct);
        await UpsertPreviousWriterRelayAsync(
            ProjectionScopeStatusRoutes.BuildLegacyActorId(Id),
            legacyKind,
            ct);
        return true;
    }

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

        var expectedWriterActorId = ResolveWarmingWriterActorId(route);
        var requiredObservedVersion = Math.Max(
            route.WarmStartedVersion,
            route.WarmingProbeVersion);
        if (!string.Equals(evt.WriterActorId, expectedWriterActorId, StringComparison.Ordinal) ||
            !IsExpectedDirectPublisher(expectedWriterActorId) ||
            evt.ObservedVersion < requiredObservedVersion ||
            !StatusRouteHasAllRequiredSeals(route) ||
            await ReadActivationSealAdmissionAsync(CancellationToken.None) == null ||
            (await ReadTerminalQuiescenceAsync(CancellationToken.None)).Receipt == null)
        {
            return;
        }

        if (!HasStatusRouteCutoverLifecycleDependencies())
        {
            _logger.LogInformation(
                "Projection scope status route caught-up report cannot block the route: lifecycle dependencies unavailable. actorId={ActorId} routeEpoch={RouteEpoch}",
                Id,
                route.RouteEpoch);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt: 0, CancellationToken.None);
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

        // Use only an authenticated report that observed the post-receipt committed probe.
        if (evt.ObservedVersion >= requiredObservedVersion)
            await BlockAndCompleteCutoverAsync(State.StatusRoute!, CancellationToken.None);
    }

    private bool HasStatusRouteCutoverLifecycleDependencies()
    {
        if (Services.GetService<IActorRuntime>() == null ||
            Services.GetService<IActorDispatchPort>() == null)
        {
            return false;
        }

        var registry = Services.GetService<IAgentKindRegistry>();
        return registry != null &&
               registry.TryGetKindForAgentType(
                   typeof(ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>),
                   out _);
    }

    private async Task BlockAndCompleteCutoverAsync(ProjectionScopeStatusRoute route, CancellationToken ct)
    {
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteBlockedEvent
        {
            RouteEpoch = route.RouteEpoch,
            BlockedVersion = CurrentScopeVersion + 1,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
        await CompleteCutoverAsync(State.StatusRoute!, attempt: 0, ct);
    }

    /// <summary>
    /// Re-dispatches the strengthened release while BLOCKED. Dispatch admission is never drain
    /// evidence; only the exact writer's authenticated confirmation can flip the route.
    /// </summary>
    private async Task CompleteCutoverAsync(ProjectionScopeStatusRoute route, int attempt, CancellationToken ct)
    {
        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime == null ||
            dispatchPort == null ||
            !await InstallWarmingWriterAsync(route, runtime, dispatchPort, ct))
        {
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }

        if (!await RestorePreviousWriterForDrainAsync(route, ct))
        {
            _logger.LogInformation(
                "Projection scope status route cannot restore its previous writer for a fresh drain probe. actorId={ActorId} routeEpoch={RouteEpoch} attempt={Attempt}",
                Id,
                route.RouteEpoch,
                attempt);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }

        var requiredObservedVersion = route.DrainProbeVersion > 0
            ? route.DrainProbeVersion
            : CurrentScopeVersion + 1;
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteDrainProbedEvent
        {
            RouteEpoch = route.RouteEpoch,
            RequiredObservedVersion = requiredObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });

        route = State.StatusRoute!;
        _ = await RequestPreviousWriterReleaseAsync(route, ct);
        _logger.LogInformation(
            "Projection scope status route stays blocked until the exact previous writer confirms the drain watermark. actorId={ActorId} routeEpoch={RouteEpoch} blockedVersion={BlockedVersion} drainProbeVersion={DrainProbeVersion} attempt={Attempt}",
            Id,
            route.RouteEpoch,
            route.BlockedVersion,
            route.DrainProbeVersion,
            attempt);
        await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
    }

    private async Task<bool> RestorePreviousWriterForDrainAsync(
        ProjectionScopeStatusRoute route,
        CancellationToken ct)
    {
        if (route.Phase != ProjectionScopeStatusRoutePhase.Blocked)
            return false;

        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime == null || dispatchPort == null)
            return false;

        var previousWriterActorId = ResolvePreviousWriterActorId(route);
        if (ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route))
        {
            var registry = Services.GetService<IAgentKindRegistry>();
            if (registry == null ||
                !registry.TryGetKindForAgentType(
                    typeof(ProjectionMaterializationScopeGAgent<ProjectionScopeStatusMaterializationContext>),
                    out var legacyKind))
            {
                return false;
            }

            if (!await runtime.ExistsAsync(previousWriterActorId))
                _ = await runtime.CreateByKindAsync(legacyKind, previousWriterActorId, ct);

            await DispatchEnsureLifecycleAsync(
                dispatchPort,
                previousWriterActorId,
                new EnsureProjectionScopeCommand
                {
                    RootActorId = Id,
                    ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                    Mode = ProjectionScopeMode.DurableMaterialization,
                },
                ct);
            await UpsertPreviousWriterRelayAsync(previousWriterActorId, legacyKind, ct);
            return true;
        }

        if (!ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route))
            return false;

        if (!await runtime.ExistsAsync(previousWriterActorId))
            _ = await runtime.CreateByKindAsync(ProjectionScopeStatusGAgent.AgentKind, previousWriterActorId, ct);

        await DispatchEnsureLifecycleAsync(
            dispatchPort,
            previousWriterActorId,
            new EnsureProjectionScopeCommand
            {
                RootActorId = Id,
                ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            },
            ct);
        await UpsertPreviousWriterRelayAsync(
            previousWriterActorId,
            ProjectionScopeStatusGAgent.AgentKind,
            ct);
        return true;
    }

    private Task UpsertPreviousWriterRelayAsync(
        string previousWriterActorId,
        string previousWriterKind,
        CancellationToken ct) =>
        Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(Id)
            .UpsertRelayAsync(
                ProjectionScopeObservationRelayBinding.Create(
                    Id,
                    previousWriterActorId,
                    previousWriterKind,
                    activationGeneration: 1),
                ct);

    private async Task<bool> RequestPreviousWriterReleaseAsync(
        ProjectionScopeStatusRoute route,
        CancellationToken ct)
    {
        if (route.Phase != ProjectionScopeStatusRoutePhase.Blocked ||
            ResolveRequiredDrainVersion(route) <= 0)
            return false;

        var previousWriterActorId = ResolvePreviousWriterActorId(route);
        var runtime = Services.GetService<IActorRuntime>();
        if (runtime == null || !await runtime.ExistsAsync(previousWriterActorId))
            return false;

        await SendToAsync(
            previousWriterActorId,
            BuildPreviousWriterReleaseCommand(route, previousWriterActorId),
            ct);
        return true;
    }

    [EventHandler]
    public async Task HandleStatusWriterReleasedAsync(ProjectionScopeStatusWriterReleasedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (!OwnsStatusRoute)
            return;

        var route = State.StatusRoute;
        if (route == null ||
            route.Phase != ProjectionScopeStatusRoutePhase.Blocked ||
            ResolveRequiredDrainVersion(route) <= 0 ||
            route.RouteEpoch != evt.RouteEpoch ||
            !string.Equals(evt.SourceScopeActorId, Id, StringComparison.Ordinal))
        {
            return;
        }

        var expectedWriterActorId = ResolvePreviousWriterActorId(route);
        if (!string.Equals(evt.WriterActorId, expectedWriterActorId, StringComparison.Ordinal) ||
            !IsExpectedDirectPublisher(expectedWriterActorId) ||
            evt.LastObservedVersion < ResolveRequiredDrainVersion(route) ||
            !StatusRouteHasAllRequiredSeals(route) ||
            await ReadActivationSealAdmissionAsync(CancellationToken.None) == null ||
            (await ReadTerminalQuiescenceAsync(CancellationToken.None)).Receipt == null)
        {
            return;
        }

        await RemoveStatusRelayAsync(expectedWriterActorId, CancellationToken.None);
        await PersistDomainEventAsync(new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = route.RouteEpoch,
            ReleasedWriterObservedVersion = evt.LastObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
        await ActivateStatusRouteAsync(State.StatusRoute!);
    }

    private async Task ActivateStatusRouteAsync(ProjectionScopeStatusRoute route)
    {
        var flipped = route.Clone();
        flipped.Phase = ProjectionScopeStatusRoutePhase.Active;
        flipped.FlipVersion = CurrentScopeVersion + 1;
        flipped.LegacyRouteReleased = true;
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = flipped,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
    }

    private string ResolveWarmingWriterActorId(ProjectionScopeStatusRoute route) =>
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route)
            ? ProjectionScopeStatusRoutes.BuildTerminalActorId(Id)
            : ProjectionScopeStatusRoutes.BuildLegacyActorId(Id);

    private string ResolvePreviousWriterActorId(ProjectionScopeStatusRoute route) =>
        ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route)
            ? ProjectionScopeStatusRoutes.BuildLegacyActorId(Id)
            : ProjectionScopeStatusRoutes.BuildTerminalActorId(Id);

    private static long ResolveRequiredDrainVersion(ProjectionScopeStatusRoute route) =>
        Math.Max(route.BlockedVersion, route.DrainProbeVersion);

    private ReleaseProjectionScopeCommand BuildPreviousWriterReleaseCommand(
        ProjectionScopeStatusRoute route,
        string previousWriterActorId) =>
        new()
        {
            RootActorId = Id,
            ProjectionKind = ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route)
                ? ProjectionScopeStatusMaterializationContext.ProjectionKindValue
                : ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
            Mode = ProjectionScopeMode.DurableMaterialization,
            StatusRouteEpoch = route.RouteEpoch,
            ExpectedWriterActorId = previousWriterActorId,
            RequiredObservedVersion = ResolveRequiredDrainVersion(route),
        };

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

        var sourceScopeActorId = BuildObservedSourceScopeActorId(sourceState);
        if (!IsAuthenticForwardedSourcePublication(envelope, stateEvent, sourceScopeActorId) ||
            !string.Equals(sourceScopeActorId, State.RootActorId, StringComparison.Ordinal))
        {
            return true;
        }

        var route = sourceState.StatusRoute;
        if (route != null &&
            (route.Phase is ProjectionScopeStatusRoutePhase.Warming or
                ProjectionScopeStatusRoutePhase.Blocked))
        {
            if (!HasLegacyWriterBoundPhaseBSeals(route, sourceScopeActorId))
                return true;

            if (!await HasLegacyWriterLivePhaseBProofsAsync())
            {
                throw new ProjectionScopeStatusPhaseBProofUnavailableException(
                    Id,
                    sourceScopeActorId,
                    route.RouteEpoch,
                    route.Phase,
                    ProjectionScopeStatusActorRole.LegacyWriter);
            }
        }

        if (ProjectionScopeStatusRoutePolicy.LegacyShadowIsSuperseded(route))
        {
            if (route!.Phase == ProjectionScopeStatusRoutePhase.Blocked)
            {
                // The forwarded BLOCKED publication is the only drain proof. Commit its exact
                // source version before confirming; a racing release command cannot fabricate it.
                var blockedDrainVersion = Math.Max(
                    State.HighestSeenVersion,
                    stateEvent?.Version ?? 0);
                await ReleaseScopeAsync(blockedDrainVersion);
                await ConfirmStatusWriterReleasedAsync(
                    sourceScopeActorId,
                    route.RouteEpoch,
                    State.ReleasedAtObservedVersion);
                return true;
            }

            _logger.LogInformation(
                "Legacy status shadow observed a source whose status route selects the terminal materializer; releasing itself. actorId={ActorId} routeEpoch={RouteEpoch}",
                Id,
                route.RouteEpoch);
            // This publication is the last one the shadow observed through the source's relay
            // (stream order). Persist that watermark with the release so a lost confirmation is
            // re-sent from durable evidence rather than the shadow's pre-release live state.
            var releasedAtObservedVersion = Math.Max(State.HighestSeenVersion, stateEvent?.Version ?? 0);
            await ReleaseScopeAsync(releasedAtObservedVersion);
            return true;
        }

        if (ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route) &&
            route!.Phase == ProjectionScopeStatusRoutePhase.Warming &&
            stateEvent != null)
        {
            await SendToAsync(sourceScopeActorId, new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = sourceScopeActorId,
                RouteEpoch = route.RouteEpoch,
                ObservedVersion = stateEvent.Version,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now()),
                WriterActorId = Id,
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
    private async Task ReconcileReleasedPreviousWriterRelayAsync(ProjectionScopeStatusRoute route, CancellationToken ct)
    {
        if (!route.LegacyRouteReleased)
            return;

        var authority = Services.GetService<IStreamForwardingBindingAuthority>();
        if (authority == null)
            return;

        var previousWriterActorId = ResolvePreviousWriterActorId(route);
        var binding = await authority.GetAsync(Id, previousWriterActorId, ct);
        if (binding == null)
            return;

        _logger.LogWarning(
            "Projection scope previous-writer relay exists after its durable release; removing the contradictory relay without re-dispatching release. actorId={ActorId} previousWriterActorId={PreviousWriterActorId} routeEpoch={RouteEpoch}",
            Id,
            previousWriterActorId,
            route.RouteEpoch);
        await RemoveStatusRelayAsync(previousWriterActorId, ct);
    }

    // ── durable adoption retry ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The retry is a backed-off self continuation through the durable callback scheduler. It
    /// observes the historical bridge receipt and resumes route preparation or repair; the attempt
    /// count travels in the command, never in actor memory.
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

        await DispatchEnsureLifecycleAsync(
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

        await DispatchEnsureLifecycleAsync(
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

    private Task RemoveStatusRelayAsync(string writerActorId, CancellationToken ct) =>
        Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(Id)
            .RemoveRelayAsync(writerActorId, ct);

    /// <summary>
    /// Sent by a status writer (this scope acting as the legacy shadow) to its source after its
    /// release is committed: the typed continuation the source's cutover waits for.
    /// </summary>
    private Task ConfirmStatusWriterReleasedAsync(string sourceScopeActorId, long routeEpoch, long lastObservedVersion) =>
        SendToAsync(sourceScopeActorId, new ProjectionScopeStatusWriterReleasedEvent
        {
            SourceScopeActorId = sourceScopeActorId,
            RouteEpoch = routeEpoch,
            LastObservedVersion = lastObservedVersion,
            ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now()),
            WriterActorId = Id,
        });

    private static bool IsAuthenticForwardedSourcePublication(
        EventEnvelope envelope,
        StateEvent? stateEvent,
        string sourceScopeActorId) =>
        !string.IsNullOrWhiteSpace(sourceScopeActorId) &&
        string.Equals(envelope.Route?.PublisherActorId, sourceScopeActorId, StringComparison.Ordinal) &&
        string.Equals(
            StreamForwardingEnvelopeState.GetSourceStreamId(envelope),
            sourceScopeActorId,
            StringComparison.Ordinal) &&
        string.Equals(stateEvent?.AgentId, sourceScopeActorId, StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(envelope.Runtime?.SourceActorId) ||
         string.Equals(envelope.Runtime.SourceActorId, sourceScopeActorId, StringComparison.Ordinal));

    private static string BuildObservedSourceScopeActorId(ProjectionScopeState sourceState) =>
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            sourceState.RootActorId,
            sourceState.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(sourceState.Mode),
            sourceState.SessionId));

    private async Task DispatchEnsureLifecycleAsync(
        IActorDispatchPort dispatchPort,
        string targetActorId,
        EnsureProjectionScopeCommand command,
        CancellationToken ct)
    {
        var envelope = ProjectionScopeCommandEnvelopeFactory.Create(command, targetActorId);
        envelope.Route = EnvelopeRouteSemantics.CreateDirect(Id, targetActorId);
        _ = await dispatchPort.DispatchAsync(targetActorId, envelope, ct);
    }

    // ── fleet admission ──────────────────────────────────────────────────────────────────────

    private Task<RuntimeFleetCapabilityAdmissionGrant?> ReadActivationSealAdmissionAsync(
        CancellationToken ct) =>
        ProjectionScopeStatusActivationSealPolicy.ReadFreshAdmissionAsync(Services, ct);

    private readonly record struct TerminalQuiescenceRead(
        bool ReaderAvailable,
        RuntimeFleetCapabilityQuiescenceReceipt? Receipt);

    /// <summary>
    /// Reads historical evidence that the distinct Phase-A bridge contract reached typed
    /// quiescence. This is not live admission and cannot authorize a new route or Phase-B rollout.
    /// </summary>
    private async Task<TerminalQuiescenceRead> ReadTerminalQuiescenceAsync(CancellationToken ct)
    {
        var quiescenceReader = Services.GetService<IRuntimeFleetCapabilityQuiescenceReader>();
        if (quiescenceReader == null)
            return new TerminalQuiescenceRead(false, null);

        var receipt = await RuntimeFleetCapabilityAdmissionValidation.GetQuiescenceReceiptAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
            RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion,
            quiescenceReader,
            ct);
        return new TerminalQuiescenceRead(true, receipt);
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
        $"Projection scope '{scopeActorId}' status route epoch {routeEpoch} is blocked for cutover; the observation is retried after the flip."),
        IRuntimeEnvelopeRetryableException
{
    public string ScopeActorId { get; } = scopeActorId;

    public long RouteEpoch { get; } = routeEpoch;
}

/// <summary>
/// A WARMING/BLOCKED status publication carries exact actor-owned writer seals, but its fresh
/// fleet admission or quiescence receipt is not currently visible. The provider must redeliver
/// the exact committed publication after the live proof becomes visible; acknowledging it would
/// create a permanent projection gap.
/// </summary>
public sealed class ProjectionScopeStatusPhaseBProofUnavailableException(
    string materializerActorId,
    string sourceScopeActorId,
    long routeEpoch,
    ProjectionScopeStatusRoutePhase phase,
    ProjectionScopeStatusActorRole writerRole)
    : InvalidOperationException(
        $"Status materializer '{materializerActorId}' cannot prove Phase-B admission for source '{sourceScopeActorId}', route epoch {routeEpoch}, phase {phase}, writer role {writerRole}; the observation is redelivered."),
        IRuntimeEnvelopeRetryableException
{
    public string MaterializerActorId { get; } = materializerActorId;

    public string SourceScopeActorId { get; } = sourceScopeActorId;

    public long RouteEpoch { get; } = routeEpoch;

    public ProjectionScopeStatusRoutePhase Phase { get; } = phase;

    public ProjectionScopeStatusActorRole WriterRole { get; } = writerRole;
}
