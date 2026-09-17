using System.Diagnostics;
using Aevatar.CQRS.Projection.Core.Observability;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.EventSourcing;
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

public abstract partial class ProjectionScopeGAgentBase<TContext>
    : GAgentBase<ProjectionScopeState>
    , IEventSourcingVersionDriftRecoverableActor
    where TContext : class, IProjectionMaterializationContext
{
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
        {
            // A crash between the release commit and the relay removal in ReleaseScopeAsync
            // leaves an exact-shape relay binding behind; the publication fast path accepts it
            // and this scope then silently drops forwarded facts on every publication. Removing
            // the relay on any reactivation converges the topology and forces the next
            // publication through the cold activation path (a new generation).
            await RemoveObservationRelayAsync(State.RootActorId, ct);
            return;
        }

        // The status route decision and the legacy cleanup happen before this scope's own
        // observation relay (the activation evidence every activation service reads) is
        // (re)asserted, so no activation service can observe a half-adopted scope on either
        // the cold ensure path or the activation path.
        await AdvanceStatusRouteAsync(ct);
        await EnsureObservationRelayAsync(State.RootActorId, ct);
        if (EnablesDurableObservationRecovery && State.InFlightObservation?.Source != null)
            await ScheduleInFlightObservationRecoveryAsync(ct);
        else
            await ScheduleFailureRecoveryAsync(ct);

        await OnScopeReadyAsync(ct);
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
        await AdvanceStatusRouteAsync(CancellationToken.None);

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

        if (command.StatusRouteEpoch > 0)
        {
            if (!IsExpectedDirectPublisher(command.RootActorId) ||
                !HasSameScope(command) ||
                !string.Equals(command.ExpectedWriterActorId, Id, StringComparison.Ordinal) ||
                command.RequiredObservedVersion <= 0)
            {
                return;
            }

            var durableObservedVersion = State.Released
                ? State.ReleasedAtObservedVersion
                : State.HighestSeenVersion;
            if (durableObservedVersion < command.RequiredObservedVersion)
                return;

            await ReleaseScopeAsync(durableObservedVersion);

            // A status-route cutover release is confirmed to the source only after the release is
            // committed (also when it already was): the source flips the route on this typed
            // continuation, never on inbox acceptance of the command.
            await ConfirmStatusWriterReleasedAsync(
                command.RootActorId,
                command.StatusRouteEpoch,
                State.ReleasedAtObservedVersion);
            return;
        }

        await ReleaseScopeAsync(State.HighestSeenVersion);
    }

    private bool IsExpectedDirectPublisher(string expectedActorId)
    {
        var inbound = ActiveInboundEnvelope;
        if (inbound == null)
            return false;

        var runtimeSourceActorId = inbound.Runtime?.SourceActorId;
        return inbound.Route.IsDirect() &&
               string.Equals(inbound.Route?.PublisherActorId, expectedActorId, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(runtimeSourceActorId) &&
               string.Equals(runtimeSourceActorId, expectedActorId, StringComparison.Ordinal);
    }

    private bool HasSameScope(ReleaseProjectionScopeCommand command) =>
        string.Equals(command.RootActorId, State.RootActorId, StringComparison.Ordinal) &&
        string.Equals(command.ProjectionKind, State.ProjectionKind, StringComparison.Ordinal) &&
        string.Equals(command.SessionId, State.SessionId, StringComparison.Ordinal) &&
        command.Mode == State.Mode;

    private async Task ReleaseScopeAsync(long lastObservedVersion)
    {
        if (!State.Active)
            return;

        if (!State.Released)
        {
            await PersistDomainEventAsync(new ProjectionScopeReleasedEvent
            {
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                LastObservedVersion = lastObservedVersion,
            });
        }

        // Relay removal happens after the release commit. Repeating it for an already released
        // scope closes the crash window between those two operations before a route cutover is
        // confirmed to the source.
        await RemoveObservationRelayAsync(State.RootActorId, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleReplayAsync(ReplayProjectionFailuresCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!State.Active || State.Released || State.Failures.Count == 0)
            return;

        // Durable observations are strictly serialized. Replaying a later failure while an
        // earlier source is still staged makes DispatchObservationAsync recover the staged
        // source first; if that recovery fails, the replay tracker would otherwise charge the
        // later failure for the earlier source's attempt. Resume the actor-owned observation
        // before admitting any backlog item.
        if (EnablesDurableObservationRecovery && State.InFlightObservation?.Source != null)
        {
            await ScheduleInFlightObservationRecoveryAsync(CancellationToken.None);
            return;
        }

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

        // A failed replay can leave its own source durably staged. Finish that exact source
        // before another backlog item is attempted, preserving source ownership of retry counts.
        if (EnablesDurableObservationRecovery && State.InFlightObservation?.Source != null)
            await ScheduleInFlightObservationRecoveryAsync(CancellationToken.None);
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

        if (await ReconcileStatusRouteOnObservationAsync(envelope))
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
            .On<ProjectionMaterializationCutoverAbortedEvent>(
                ProjectionScopeStateApplier.ApplyMaterializationCutoverAborted)
            .On<ProjectionMaterializationRouteRolledBackEvent>(
                ProjectionScopeStateApplier.ApplyMaterializationRouteRolledBack)
            .On<ProjectionScopeStatusRoutePreparationStartedEvent>(
                ProjectionScopeStateApplier.ApplyStatusRoutePreparationStarted)
            .On<ProjectionScopeStatusActorSealRecordedEvent>(
                ProjectionScopeStateApplier.ApplyStatusActorSealRecorded)
            .On<ProjectionScopeStatusRouteActivationSealsBoundEvent>(
                ProjectionScopeStateApplier.ApplyStatusRouteActivationSealsBound)
            .On<ProjectionScopeStatusRouteWarmingStartedEvent>(ProjectionScopeStateApplier.ApplyStatusRouteWarmingStarted)
            .On<ProjectionScopeStatusRouteWarmingProbedEvent>(ProjectionScopeStateApplier.ApplyStatusRouteWarmingProbed)
            .On<ProjectionScopeStatusRouteCaughtUpEvent>(ProjectionScopeStateApplier.ApplyStatusRouteCaughtUp)
            .On<ProjectionScopeStatusRouteBlockedEvent>(ProjectionScopeStateApplier.ApplyStatusRouteBlocked)
            .On<ProjectionScopeStatusRouteDrainProbedEvent>(ProjectionScopeStateApplier.ApplyStatusRouteDrainProbed)
            .On<ProjectionScopeStatusRouteActivatedEvent>(ProjectionScopeStateApplier.ApplyStatusRouteActivated)
            .On<ProjectionScopeStatusLegacyRouteReleasedEvent>(ProjectionScopeStateApplier.ApplyStatusLegacyRouteReleased)
            .On<ProjectionScopeStatusRouteContractUpgradedEvent>(ProjectionScopeStateApplier.ApplyStatusRouteContractUpgraded)
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
        // A blocked status route refuses every dispatch — observed, replayed or resumed — so
        // nothing new is published until the cutover flips; the caller redelivers.
        if (State.StatusRoute?.Phase == ProjectionScopeStatusRoutePhase.Blocked)
            throw new ProjectionScopeStatusRouteBlockedException(Id, State.StatusRoute.RouteEpoch);

        var context = ResolveScopeContext();
        var sourceActorId = ResolveSourceActorId(envelope);
        var eventKind = ResolveEventKind(envelope);
        var observedMetadata = BuildObservedEnvelopeMetadata(envelope);
        var durableSource = EnablesDurableObservationRecovery
            ? BuildRequiredSourceCoordinate(sourceActorId, observedMetadata)
            : null;
        if (durableSource != null && origin != ProjectionObservationDispatchOrigin.InFlightRecovery)
            await RecoverBlockingInFlightObservationAsync(durableSource, ct);

        if (origin == ProjectionObservationDispatchOrigin.Observed)
        {
            await PersistDomainEventAsync(new ProjectionScopeEnvelopeReceivedEvent
            {
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                EventKind = eventKind,
            });
            ProjectionProcessingMetrics.RecordReceived(State.ProjectionKind, eventKind);
        }

        var admission = DurableSourceAdmission.Admitted;
        if (durableSource != null)
        {
            admission = AdmitDurableSource(durableSource);
            if (admission is DurableSourceAdmission.ExactDuplicate or
                DurableSourceAdmission.FencedStale)
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

        if (durableSource != null &&
            (State.InFlightObservation?.Source == null ||
             admission == DurableSourceAdmission.MaintenanceSupersession))
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

    private async Task RecoverBlockingInFlightObservationAsync(
        ProjectionSourceCoordinate received,
        CancellationToken ct)
    {
        var pending = State.InFlightObservation;
        if (pending?.Source == null ||
            pending.Envelope == null ||
            HasSameSource(pending.Source, received) ||
            CanMaintenanceSupersede(pending.Source, received))
        {
            return;
        }

        // Transport retries do not guarantee that the staged source is redelivered before
        // newer observations. Finish the actor-owned durable observation in this turn so the
        // current envelope can be admitted only after its predecessor advances the watermark.
        await DispatchObservationAsync(
            pending.Envelope,
            ct,
            ProjectionObservationDispatchOrigin.InFlightRecovery);
        await ScheduleFailureRecoveryAsync(ct);
    }

    private DurableSourceAdmission AdmitDurableSource(ProjectionSourceCoordinate received)
    {
        if (State.LastSuccessfulSourceCoordinatesByActor.TryGetValue(received.ActorId, out var committed))
        {
            if (received.StateVersion < committed.StateVersion)
                return DurableSourceAdmission.FencedStale;

            if (committed.StateVersion == received.StateVersion)
            {
                if (string.Equals(committed.EventId, received.EventId, StringComparison.Ordinal))
                    return DurableSourceAdmission.ExactDuplicate;

                var precedence = ProjectionWriteResultEvaluator
                    .EvaluateSameVersionMaintenancePrecedence(committed.EventId, received.EventId);
                if (precedence?.Disposition == ProjectionWriteDisposition.Stale)
                    return DurableSourceAdmission.FencedStale;
                if (precedence?.Disposition != ProjectionWriteDisposition.Applied)
                    throw new ProjectionSourceCoordinateConflictException(committed, received);

                return DurableSourceAdmission.MaintenanceSupersession;
            }
        }

        var pending = State.InFlightObservation?.Source;
        if (pending == null)
            return DurableSourceAdmission.Admitted;

        if (HasSameSource(pending, received))
            return DurableSourceAdmission.InFlightResume;

        if (CanMaintenanceSupersede(pending, received))
            return DurableSourceAdmission.MaintenanceSupersession;

        throw new ProjectionScopeInFlightObservationPendingException(pending, received);
    }

    private static bool CanMaintenanceSupersede(
        ProjectionSourceCoordinate pending,
        ProjectionSourceCoordinate received) =>
        string.Equals(pending.ActorId, received.ActorId, StringComparison.Ordinal) &&
        received.StateVersion >= pending.StateVersion &&
        CommittedStateRepublish.IsRepublishEventId(received.EventId) &&
        (received.StateVersion > pending.StateVersion ||
         !CommittedStateRepublish.IsRepublishEventId(pending.EventId));

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
    MaintenanceSupersession = 3,
    FencedStale = 4,
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
