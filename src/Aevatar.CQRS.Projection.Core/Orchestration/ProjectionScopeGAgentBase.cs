using System.Diagnostics;
using Aevatar.CQRS.Projection.Core.Observability;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Runtime;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public abstract class ProjectionScopeGAgentBase<TContext>
    : GAgentBase<ProjectionScopeState>
    , IEventSourcingVersionDriftRecoverableActor
    where TContext : class, IProjectionMaterializationContext
{
    internal const string StatusRouteAdoptionRetryCallbackId = "projection-scope-status-route-adoption";

    /// <summary>
    /// Backed-off retry schedule for adopting the terminal status route after a scope activated
    /// while the fleet gate was still closed (~15 minutes in total, covering a rolling upgrade).
    /// </summary>
    internal static readonly TimeSpan[] StatusRouteAdoptionRetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8),
    ];

    private ILogger _logger = NullLogger.Instance;
    private ProjectionScopeFailureTracker? _failureTracker;
    private bool _isReplayingFailure;

    protected abstract ProjectionRuntimeMode RuntimeMode { get; }

    protected virtual bool EnablesDurableObservationRecovery => false;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        _logger = Services.GetService<ILoggerFactory>()?.CreateLogger(GetType()) ?? NullLogger.Instance;
        _failureTracker = new ProjectionScopeFailureTracker(
            evt => PersistDomainEventAsync(evt),
            () => Services.GetService<IProjectionFailureAlertSink>(),
            BuildScopeKey,
            () => State.Failures.Count,
            () => State.RetainedFailureDiagnostics.Count,
            () => State.RetainedFailureDiagnostics.FirstOrDefault(),
            ResolveOldestFailureAt,
            () => State.FailureDiagnosticDroppedTotal);

        if (!State.Active || State.Released)
            return;

        await EnsureObservationRelayAsync(State.RootActorId, ct);
        if (EnablesDurableObservationRecovery && State.InFlightObservation?.Source != null)
            await ScheduleInFlightObservationRecoveryAsync(ct);
        else
            await ScheduleFailureRecoveryAsync(ct);

        await AdoptTerminalStatusRouteAsync(ct);
        await OnScopeReadyAsync(ct);
    }

    /// <summary>
    /// Adopts the terminal status write route for this scope on its own turn: with a fresh
    /// fleet admission for the terminal contract it commits the epoch-fenced route first (the
    /// committed route is the fact; relay and materializer are derived from it and healed on
    /// every activation), then writes the terminal materializer relay on its own stream (it
    /// owns its relays, so no cross-actor wait is needed), ensures the materializer actor and
    /// releases the legacy shadow. Without a fresh grant the scope simply stays on the legacy
    /// route. Called only on the cold ensure / activation path, never per observed envelope.
    /// </summary>
    private async Task AdoptTerminalStatusRouteAsync(CancellationToken ct, int attempt = 0)
    {
        if (!State.Active || State.Released || RuntimeMode != ProjectionRuntimeMode.DurableMaterialization)
            return;
        // Status writers have no status writer of their own (no mirror of the mirror).
        if (DependencyInjection.ProjectionScopeStatusRuntimeRegistration.IsProjectionScopeStatusKind(State.ProjectionKind))
            return;
        var terminalActorId = ProjectionScopeStatusRoutes.BuildTerminalActorId(Id);
        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                State.StatusRoute,
                ProjectionScopeStatusGAgent.ContractId,
                ProjectionScopeStatusGAgent.ContractVersion))
        {
            // The relay is the durable evidence every activation service reads; re-asserting it
            // is idempotent and heals a lost registry entry (or a released materializer relay
            // after this scope was re-ensured) without a second route decision. The materializer
            // restarts itself on the first routed publication, so no lifecycle command is needed.
            await UpsertTerminalStatusRelayAsync(terminalActorId, ct);
            if (runtime != null && !await runtime.ExistsAsync(terminalActorId))
                _ = await runtime.CreateByKindAsync(ProjectionScopeStatusGAgent.AgentKind, terminalActorId, ct);
            await ReleaseLegacyStatusRouteIfPendingAsync(ct);
            return;
        }

        if (runtime == null || dispatchPort == null)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy: actor runtime ports unavailable. actorId={ActorId}",
                Id);
            return;
        }

        var admissionReader = Services.GetService<IRuntimeFleetCapabilityAdmissionReader>();
        var membershipReader = Services.GetService<IRuntimeLocalMembershipIdentityReader>();
        if (admissionReader == null || membershipReader == null)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy: fleet admission readers unavailable. actorId={ActorId}",
                Id);
            return;
        }

        var grant = await RuntimeFleetCapabilityAdmissionValidation.GetGrantedAdmissionAsync(
            RuntimeFleetCapability.ProjectionScopeStatusTerminalV1,
            ProjectionScopeStatusGAgent.ContractId,
            (int)ProjectionScopeStatusGAgent.ContractVersion,
            admissionReader,
            membershipReader,
            Services.GetService<TimeProvider>(),
            Services.GetService<RuntimeActorStateMigrationAdmissionOptions>(),
            ct);
        if (grant == null)
        {
            _logger.LogInformation(
                "Projection scope status route stays legacy: no fresh terminal admission. actorId={ActorId} attempt={Attempt}",
                Id,
                attempt);
            await ScheduleStatusRouteAdoptionRetryAsync(attempt, ct);
            return;
        }

        var admission = grant.Admission;
        var route = ProjectionScopeStatusRoutePolicy.BuildTerminalRoute(
            (State.StatusRoute?.RouteEpoch ?? 0) + 1);
        route.ActivatedAtUtc = Timestamp.FromDateTimeOffset(grant.ValidatedAt);
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
        await PersistDomainEventAsync(new ProjectionScopeStatusRouteActivatedEvent
        {
            Route = route,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(grant.ValidatedAt),
        });
        _logger.LogInformation(
            "Projection scope status route adopted terminal materializer. actorId={ActorId} routeEpoch={RouteEpoch}",
            Id,
            route.RouteEpoch);

        await UpsertTerminalStatusRelayAsync(terminalActorId, ct);
        await EnsureTerminalStatusMaterializerAsync(runtime, dispatchPort, terminalActorId, ct);
        await ReleaseLegacyStatusRouteIfPendingAsync(ct);
    }

    /// <summary>
    /// A durable scope that activates before the terminal fleet gate opens (a long-lived
    /// scope right after silo boot, or during a rolling upgrade) would otherwise stay on the
    /// legacy route until its next activation, which for an always-active scope never comes.
    /// The retry is a bounded, backed-off self continuation through the durable callback
    /// scheduler; the attempt count travels in the command, never in actor memory.
    /// </summary>
    private async Task ScheduleStatusRouteAdoptionRetryAsync(int attempt, CancellationToken ct)
    {
        if (attempt >= StatusRouteAdoptionRetryDelays.Length ||
            Services.GetService<IActorRuntimeCallbackScheduler>() == null)
        {
            return;
        }

        var nextAttempt = attempt + 1;
        _ = await ScheduleSelfDurableTimeoutAsync(
            StatusRouteAdoptionRetryCallbackId,
            StatusRouteAdoptionRetryDelays[attempt],
            new RetryProjectionScopeStatusRouteAdoptionCommand { Attempt = nextAttempt },
            ct: ct);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public Task HandleRetryStatusRouteAdoptionAsync(RetryProjectionScopeStatusRouteAdoptionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AdoptTerminalStatusRouteAsync(CancellationToken.None, command.Attempt);
    }

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

        var ensure = ProjectionScopeCommandEnvelopeFactory.Create(
            new EnsureProjectionScopeCommand
            {
                RootActorId = Id,
                ProjectionKind = ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                Mode = ProjectionScopeMode.DurableMaterialization,
            },
            terminalActorId);
        ensure.Route = EnvelopeRouteSemantics.CreateDirect(Id, terminalActorId);
        _ = await dispatchPort.DispatchAsync(terminalActorId, ensure, ct);
    }

    /// <summary>
    /// After the route flip the legacy status shadow of this scope is no longer a writer;
    /// remove its relay from this scope's stream and release the shadow scope actor if it
    /// exists. Retried on activation until it is durably recorded.
    /// </summary>
    private async Task ReleaseLegacyStatusRouteIfPendingAsync(CancellationToken ct)
    {
        var route = State.StatusRoute;
        if (!ProjectionScopeStatusRoutePolicy.IsTerminalRoute(route) || route!.LegacyRouteReleased)
            return;

        var legacyActorId = ProjectionScopeStatusRoutes.BuildLegacyActorId(Id);
        await Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(Id)
            .RemoveRelayAsync(legacyActorId, ct);

        var runtime = Services.GetService<IActorRuntime>();
        var dispatchPort = Services.GetService<IActorDispatchPort>();
        if (runtime != null && dispatchPort != null && await runtime.ExistsAsync(legacyActorId))
        {
            var release = ProjectionScopeCommandEnvelopeFactory.Create(
                new ReleaseProjectionScopeCommand
                {
                    RootActorId = Id,
                    ProjectionKind = ProjectionScopeStatusMaterializationContext.ProjectionKindValue,
                    Mode = ProjectionScopeMode.DurableMaterialization,
                },
                legacyActorId);
            release.Route = EnvelopeRouteSemantics.CreateDirect(Id, legacyActorId);
            _ = await dispatchPort.DispatchAsync(legacyActorId, release, ct);
        }

        await PersistDomainEventAsync(new ProjectionScopeStatusLegacyRouteReleasedEvent
        {
            RouteEpoch = route.RouteEpoch,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    protected override async Task OnCommittedStatePublicationRecoveredAsync(
        EventEnvelope envelope,
        CancellationToken ct)
    {
        await base.OnCommittedStatePublicationRecoveredAsync(envelope, ct);
        await ScheduleFailureRecoveryAsync(ct);
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        // A durable scope outlives a transient actor activation. Its relay is removed by
        // explicit release; retaining it here also avoids a publication gap during rollover.
        if (RuntimeMode != ProjectionRuntimeMode.DurableMaterialization ||
            !State.Active ||
            State.Released)
        {
            await RemoveObservationRelayAsync(State.RootActorId, ct);
        }

        await base.OnDeactivateAsync(ct);
    }

    [EventHandler]
    public async Task HandleEnsureAsync(EnsureProjectionScopeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!State.Active || State.Released)
        {
            await PersistDomainEventAsync(new ProjectionScopeStartedEvent
            {
                RootActorId = command.RootActorId ?? string.Empty,
                ProjectionKind = command.ProjectionKind ?? string.Empty,
                SessionId = command.SessionId ?? string.Empty,
                Mode = command.Mode,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                ActivationGeneration = Math.Max(1, State.ActivationGeneration + 1),
            });
        }
        else if (State.ActivationGeneration == 0)
        {
            await PersistDomainEventAsync(new ProjectionScopeActivationGenerationMigratedEvent
            {
                ActivationGeneration = 1,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }

        // The status route is decided before this scope's own observation relay becomes visible:
        // that relay is the activation evidence the activation service waits for, and it must
        // not observe a half-adopted scope (it would ensure the legacy shadow this scope is
        // about to release).
        await AdoptTerminalStatusRouteAsync(CancellationToken.None);

        await EnsureObservationRelayAsync(command.RootActorId, CancellationToken.None);
        if (!State.ObservationAttached)
        {
            await PersistDomainEventAsync(new ProjectionObservationAttachmentUpdatedEvent
            {
                Attached = true,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }

        await OnScopeReadyAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleReleaseAsync(ReleaseProjectionScopeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!State.Active || State.Released)
            return;

        await PersistDomainEventAsync(new ProjectionScopeReleasedEvent
        {
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        });

        if (State.ObservationAttached)
        {
            await PersistDomainEventAsync(new ProjectionObservationAttachmentUpdatedEvent
            {
                Attached = false,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }

        await RemoveObservationRelayAsync(State.RootActorId, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleReplayAsync(ReplayProjectionFailuresCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!State.Active || State.Released || State.Failures.Count == 0)
            return;

        if (command.AutomaticRecovery)
        {
            var observedScopeStateVersion = command.ObservedScopeStateVersion > 0
                ? command.ObservedScopeStateVersion
                : Math.Max(1, EventSourcing?.CurrentVersion ?? 0);
            if (observedScopeStateVersion <= State.LastAutomaticRecoveryObservedStateVersion)
                return;

            if (!State.Failures.Any(static failure => !failure.RetryExhausted))
                return;

            await PersistDomainEventAsync(new ProjectionScopeAutomaticRecoveryRequestedEvent
            {
                ObservedScopeStateVersion = observedScopeStateVersion,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }

        await _failureTracker!.ReplayAsync(
            State,
            command.MaxItems,
            (envelope, ct) => DispatchObservationAsync(
                envelope,
                ct,
                ProjectionObservationDispatchOrigin.FailureReplay),
            includeRetryExhausted: !command.AutomaticRecovery);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleResumeInFlightObservationAsync(
        ResumeProjectionInFlightObservationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!EnablesDurableObservationRecovery || !State.Active || State.Released)
            return;

        var pending = State.InFlightObservation;
        if (pending?.Source == null || pending.Envelope == null)
            return;

        if (command.ExpectedSource == null ||
            !HasSameSource(command.ExpectedSource, pending.Source))
        {
            return;
        }

        await DispatchObservationAsync(
            pending.Envelope,
            CancellationToken.None,
            ProjectionObservationDispatchOrigin.InFlightRecovery);
        await ScheduleFailureRecoveryAsync(CancellationToken.None);
    }

    private Task ScheduleFailureRecoveryAsync(CancellationToken ct)
    {
        if (!State.Active || State.Released)
            return Task.CompletedTask;

        var pendingCount = State.Failures.Count(static failure => !failure.RetryExhausted);
        if (pendingCount == 0)
            return Task.CompletedTask;

        return PublishAsync(
            new ReplayProjectionFailuresCommand
            {
                MaxItems = pendingCount,
                AutomaticRecovery = true,
                ObservedScopeStateVersion = Math.Max(1, EventSourcing?.CurrentVersion ?? 0),
            },
            TopologyAudience.Self,
            ct);
    }

    private Task ScheduleInFlightObservationRecoveryAsync(CancellationToken ct)
    {
        var source = State.InFlightObservation?.Source;
        if (!EnablesDurableObservationRecovery ||
            !State.Active ||
            State.Released ||
            source == null)
        {
            return Task.CompletedTask;
        }

        return PublishAsync(
            new ResumeProjectionInFlightObservationCommand
            {
                ExpectedSource = source.Clone(),
            },
            TopologyAudience.Self,
            ct);
    }

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleObservedEnvelopeAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!State.Active || State.Released)
            return;

        if (!envelope.Route.IsObserverPublication())
            return;

        if (!StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, Id) ||
            StreamForwardingRules.IsTransitOnlyForwarding(envelope))
            return;

        try
        {
            await DispatchObservationAsync(envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var durableRecoveryBlocked =
                EnablesDurableObservationRecovery && State.InFlightObservation?.Source != null;
            if (ProjectionObservationFailurePolicy.ShouldPropagate(ex) || durableRecoveryBlocked)
            {
                // Discard only uncommitted OCC suffixes. Durable observation staging
                // is already committed and must survive retry or actor reactivation.
                if (ProjectionObservationFailurePolicy.ContainsOcc(ex))
                    EventSourcing?.DiscardPendingEvents();

                _logger.LogWarning(
                    ex,
                    "Projection scope observation handling hit a retryable failure; pending events discarded. actorId={ActorId} projectionKind={ProjectionKind}",
                    Id,
                    State.ProjectionKind);
                throw;
            }

            _logger.LogWarning(
                ex,
                "Projection scope observation handling failed. actorId={ActorId} projectionKind={ProjectionKind} sessionId={SessionId}",
                Id,
                State.ProjectionKind,
                State.SessionId);
        }
    }

    protected override ProjectionScopeState TransitionState(ProjectionScopeState current, Google.Protobuf.IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ProjectionScopeStartedEvent>(ProjectionScopeStateApplier.ApplyStarted)
            .On<ProjectionScopeActivationGenerationMigratedEvent>(
                ProjectionScopeStateApplier.ApplyActivationGenerationMigrated)
            .On<ProjectionObservationAttachmentUpdatedEvent>(ProjectionScopeStateApplier.ApplyAttachmentUpdated)
            .On<ProjectionScopeReleasedEvent>(ProjectionScopeStateApplier.ApplyReleased)
            .On<ProjectionScopeEnvelopeReceivedEvent>(ProjectionScopeStateApplier.ApplyEnvelopeReceived)
            .On<ProjectionScopeEnvelopeAttemptedEvent>(ProjectionScopeStateApplier.ApplyEnvelopeAttempted)
            .On<ProjectionScopeObservationStagedEvent>(ProjectionScopeStateApplier.ApplyObservationStaged)
            .On<ProjectionScopeWatermarkAdvancedEvent>(ProjectionScopeStateApplier.ApplyWatermarkAdvanced)
            .On<ProjectionScopeDispatchFailedEvent>(ProjectionScopeStateApplier.ApplyDispatchFailed)
            .On<ProjectionScopeFailureReplayedEvent>(ProjectionScopeStateApplier.ApplyFailureReplayed)
            .On<ProjectionScopeAutomaticRecoveryRequestedEvent>(
                ProjectionScopeStateApplier.ApplyAutomaticRecoveryRequested)
            .On<ProjectionMaterializationRouteInitializedEvent>(
                ProjectionScopeStateApplier.ApplyMaterializationRouteInitialized)
            .On<ProjectionMaterializationCutoverRequestedEvent>(
                ProjectionScopeStateApplier.ApplyMaterializationCutoverRequested)
            .On<ProjectionMaterializationCutoverCandidateBuiltEvent>(
                ProjectionScopeStateApplier.ApplyMaterializationCutoverCandidateBuilt)
            .On<ProjectionMaterializationCutoverGoldenVerifiedEvent>(
                ProjectionScopeStateApplier.ApplyMaterializationCutoverGoldenVerified)
            .On<ProjectionMaterializationCutoverActivatedEvent>(
                ProjectionScopeStateApplier.ApplyMaterializationCutoverActivated)
            .On<ProjectionScopeStatusRouteActivatedEvent>(ProjectionScopeStateApplier.ApplyStatusRouteActivated)
            .On<ProjectionScopeStatusLegacyRouteReleasedEvent>(ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased)
            .OrCurrent();

    protected ProjectionRuntimeScopeKey BuildScopeKey() =>
        new(
            State.RootActorId,
            State.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(State.Mode),
            State.SessionId);

    protected abstract ValueTask<ProjectionScopeDispatchResult> ProcessObservationCoreAsync(
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct);

    protected virtual ValueTask PrepareObservationContextAsync(
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct) => ValueTask.CompletedTask;

    protected virtual ValueTask OnObservationMaterializedAsync(
        TContext context,
        EventEnvelope envelope,
        ProjectionScopeDispatchResult result,
        CancellationToken ct) => ValueTask.CompletedTask;

    protected virtual ValueTask OnScopeReadyAsync(CancellationToken ct) =>
        ValueTask.CompletedTask;

    private async Task<ProjectionScopeDispatchResult> DispatchObservationAsync(
        EventEnvelope envelope,
        CancellationToken ct,
        ProjectionObservationDispatchOrigin origin = ProjectionObservationDispatchOrigin.Observed)
    {
        var context = ResolveScopeContext();
        var sourceActorId = ResolveSourceActorId(envelope);
        var eventKind = ResolveEventKind(envelope);
        var observedMetadata = BuildObservedEnvelopeMetadata(envelope);
        var durableSource = EnablesDurableObservationRecovery
            ? BuildRequiredSourceCoordinate(sourceActorId, observedMetadata)
            : null;
        if (origin == ProjectionObservationDispatchOrigin.Observed)
        {
            await PersistDomainEventAsync(new ProjectionScopeEnvelopeReceivedEvent
            {
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                EventKind = eventKind,
            });
            ProjectionProcessingMetrics.RecordReceived(State.ProjectionKind, eventKind);
        }

        if (durableSource != null)
        {
            var admission = AdmitDurableSource(durableSource);
            if (admission == DurableSourceAdmission.ExactDuplicate)
            {
                if (origin != ProjectionObservationDispatchOrigin.FailureReplay)
                    await _failureTracker!.ResolveMatchingAsync(State, durableSource);

                return ProjectionScopeDispatchResult.Success(durableSource.StateVersion, eventKind);
            }
        }
        else if (origin == ProjectionObservationDispatchOrigin.Observed &&
                 IsAlreadyProjected(sourceActorId, envelope))
        {
            return ProjectionScopeDispatchResult.Skip(envelope.Payload?.TypeUrl ?? string.Empty);
        }

        var sourceVersion = ResolveSourceVersion(envelope);
        await PersistDomainEventAsync(new ProjectionScopeEnvelopeAttemptedEvent
        {
            HighestSeenVersion = sourceVersion,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            SourceActorId = sourceActorId,
            ObservedEnvelope = observedMetadata,
            EventKind = eventKind,
        });
        ProjectionProcessingMetrics.RecordAttempted(State.ProjectionKind, eventKind);

        if (durableSource != null && State.InFlightObservation?.Source == null)
        {
            await PersistDomainEventAsync(new ProjectionScopeObservationStagedEvent
            {
                Observation = new ProjectionScopeInFlightObservation
                {
                    Source = durableSource.Clone(),
                    Envelope = envelope.Clone(),
                    EventKind = eventKind,
                    StagedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                },
            });
        }

        await PrepareObservationContextAsync(context, envelope, ct);

        var startedAt = Stopwatch.GetTimestamp();
        var previousReplayState = _isReplayingFailure;
        _isReplayingFailure = origin == ProjectionObservationDispatchOrigin.FailureReplay;
        ProjectionScopeDispatchResult result;
        try
        {
            result = await ProcessObservationCoreAsync(context, envelope, ct);
        }
        finally
        {
            _isReplayingFailure = previousReplayState;
        }
        if (!result.Handled)
            return result;

        await OnObservationMaterializedAsync(context, envelope, result, ct);

        await PersistDomainEventAsync(new ProjectionScopeWatermarkAdvancedEvent
        {
            LastSuccessfulVersion = result.SuccessfulVersion,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            SourceActorId = sourceActorId,
            ObservedEnvelope = observedMetadata,
        });
        if (durableSource != null && origin != ProjectionObservationDispatchOrigin.FailureReplay)
            await _failureTracker!.ResolveMatchingAsync(State, durableSource);
        ProjectionProcessingMetrics.RecordSucceeded(
            State.ProjectionKind,
            result.EventType,
            Stopwatch.GetElapsedTime(startedAt));
        return result;
    }

    private DurableSourceAdmission AdmitDurableSource(ProjectionSourceCoordinate received)
    {
        if (State.LastSuccessfulSourceCoordinatesByActor.TryGetValue(received.ActorId, out var committed) &&
            committed.StateVersion == received.StateVersion)
        {
            if (!string.Equals(committed.EventId, received.EventId, StringComparison.Ordinal))
                throw new ProjectionSourceCoordinateConflictException(committed, received);

            return DurableSourceAdmission.ExactDuplicate;
        }

        var pending = State.InFlightObservation?.Source;
        if (pending == null)
            return DurableSourceAdmission.Admitted;

        if (!HasSameSource(pending, received))
            throw new ProjectionScopeInFlightObservationPendingException(pending, received);

        return DurableSourceAdmission.InFlightResume;
    }

    private static ProjectionSourceCoordinate BuildRequiredSourceCoordinate(
        string sourceActorId,
        ProjectionObservedEnvelopeMetadata? observed)
    {
        if (string.IsNullOrWhiteSpace(sourceActorId))
            throw new ProjectionSourceCoordinateInvalidException("source actor id is missing");
        if (observed == null)
            throw new ProjectionSourceCoordinateInvalidException("committed observation metadata is missing");
        if (observed.StateVersion <= 0)
            throw new ProjectionSourceCoordinateInvalidException("state version must be positive");
        if (string.IsNullOrWhiteSpace(observed.EventId))
            throw new ProjectionSourceCoordinateInvalidException("event id is missing");

        return new ProjectionSourceCoordinate
        {
            ActorId = sourceActorId,
            StateVersion = observed.StateVersion,
            EventId = observed.EventId,
        };
    }

    private static bool HasSameSource(
        ProjectionSourceCoordinate left,
        ProjectionSourceCoordinate right) =>
        left.StateVersion == right.StateVersion &&
        string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) &&
        string.Equals(left.EventId, right.EventId, StringComparison.Ordinal);

    private static ProjectionObservedEnvelopeMetadata? BuildObservedEnvelopeMetadata(EventEnvelope envelope)
    {
        if (!CommittedStateEventEnvelope.TryUnpack(envelope, out var published) ||
            published?.StateEvent?.EventData == null)
        {
            return null;
        }

        return new ProjectionObservedEnvelopeMetadata
        {
            EventId = published.StateEvent.EventId ?? string.Empty,
            TypeUrl = published.StateEvent.EventData.TypeUrl ?? string.Empty,
            StateVersion = published.StateEvent.Version,
            TimestampUtc = published.StateEvent.Timestamp?.Clone(),
        };
    }

    private bool IsAlreadyProjected(string sourceActorId, EventEnvelope envelope)
    {
        if (RuntimeMode != ProjectionRuntimeMode.SessionObservation ||
            string.IsNullOrWhiteSpace(sourceActorId) ||
            !CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out _, out _, out var sourceVersion) ||
            sourceVersion <= 0)
        {
            return false;
        }

        return State.LastSuccessfulVersionsByActor.TryGetValue(sourceActorId, out var lastSuccessfulVersion) &&
               sourceVersion <= lastSuccessfulVersion;
    }

    private static string ResolveSourceActorId(EventEnvelope envelope)
    {
        var sourceActorId = CommittedStateEventEnvelope.GetOriginActorId(envelope);
        return string.IsNullOrWhiteSpace(sourceActorId)
            ? envelope.Route?.PublisherActorId ?? string.Empty
            : sourceActorId;
    }

    private static long ResolveSourceVersion(EventEnvelope envelope) =>
        CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out _, out _, out var sourceVersion)
            ? sourceVersion
            : 0;

    private static string ResolveEventKind(EventEnvelope envelope) =>
        CommittedStateEventEnvelope.TryGetObservedPayload(envelope, out var observed, out _, out _)
            ? observed?.TypeUrl ?? envelope.Payload?.TypeUrl ?? string.Empty
            : envelope.Payload?.TypeUrl ?? string.Empty;

    private DateTimeOffset? ResolveOldestFailureAt() =>
        State.Failures
            .Select(failure => failure.OccurredAtUtc?.ToDateTimeOffset())
            .Where(occurredAt => occurredAt.HasValue)
            .Select(occurredAt => occurredAt!.Value)
            .DefaultIfEmpty()
            .Min() is var oldest && oldest != default
                ? oldest
                : null;

    private TContext ResolveScopeContext()
    {
        var factory = Services.GetRequiredService<Func<ProjectionRuntimeScopeKey, TContext>>();
        return factory(BuildScopeKey());
    }

    private Task EnsureObservationRelayAsync(string? rootActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return Task.CompletedTask;

        return Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(rootActorId)
            .UpsertRelayAsync(BuildObservationRelayBinding(rootActorId), ct);
    }

    private Task RemoveObservationRelayAsync(string? rootActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rootActorId))
            return Task.CompletedTask;

        return Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(rootActorId)
            .RemoveRelayAsync(Id, ct);
    }

    private StreamForwardingBinding BuildObservationRelayBinding(string rootActorId)
    {
        var registry = Services.GetRequiredService<IAgentKindRegistry>();
        if (!registry.TryGetKindForAgentType(GetType(), out var targetActorKind))
        {
            throw new InvalidOperationException(
                $"Projection scope actor type {GetType().FullName} is not registered with a primary agent kind.");
        }

        return ProjectionScopeObservationRelayBinding.Create(
            rootActorId,
            Id,
            targetActorKind,
            State.ActivationGeneration);
    }

    protected ValueTask RecordDispatchFailureAsync(
        string stage,
        string eventId,
        string eventType,
        long sourceVersion,
        string reason,
        EventEnvelope envelope)
    {
        if (_isReplayingFailure)
            return ValueTask.CompletedTask;

        return _failureTracker!.RecordAsync(stage, eventId, eventType, sourceVersion, reason, envelope, _logger);
    }
}

internal enum ProjectionObservationDispatchOrigin
{
    Observed = 0,
    FailureReplay = 1,
    InFlightRecovery = 2,
}

internal enum DurableSourceAdmission
{
    Admitted = 0,
    InFlightResume = 1,
    ExactDuplicate = 2,
}

public readonly record struct ProjectionScopeDispatchResult(
    bool Handled,
    long SuccessfulVersion,
    string EventType)
{
    public static ProjectionScopeDispatchResult Skip(string eventType = "") =>
        new(false, 0, eventType);

    public static ProjectionScopeDispatchResult Success(
        long successfulVersion,
        string eventType) =>
        new(true, successfulVersion, eventType);
}
