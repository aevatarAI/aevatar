using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.Runtime;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Single-hop terminal status materializer for one source projection scope. It observes the
/// source scope's committed <see cref="CommittedStateEventPublished"/> publications and writes
/// <see cref="ProjectionScopeStatusDocument"/> once per terminal source outcome, only while the
/// source scope's committed status route names this contract. It keeps no per-envelope
/// bookkeeping stream: its own durable facts are lifecycle and deferred-write retries.
/// </summary>
[GAgent(AgentKind, StateSchemaVersion = SupportedStateSchemaVersion)]
public sealed class ProjectionScopeStatusGAgent
    : GAgentBase<ProjectionScopeStatusTerminalState>
{
    public const string AgentKind = "projection.scope-status-terminal";
    public const int SupportedStateSchemaVersion = 1;
    /// <summary>
    /// The contract every new route names. Routes created under an earlier terminal contract are
    /// still served by this materializer (a source keeps its status writer across the upgrade),
    /// which is decided by <see cref="ProjectionScopeStatusRoutePolicy"/>.
    /// </summary>
    public const string ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV2;

    public const long ContractVersion = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalReaderVersion;
    internal const string WriteRetryCallbackId = "projection-scope-status-write-retry";

    /// <summary>
    /// Backed-off durable retry cadence for a transiently failed status write. The last delay
    /// repeats for as long as the store stays unavailable: recovery needs no new source event,
    /// no manual ensure and no actor deactivation. Once <see cref="StalledAttemptThreshold"/>
    /// attempts have failed the pending write is marked stalled (explicit, observable) while the
    /// retries continue at the capped cadence.
    /// </summary>
    internal static readonly TimeSpan[] WriteRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];

    internal const int StalledAttemptThreshold = 5;

    private ILogger _logger = NullLogger.Instance;

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        _logger = Services.GetService<ILoggerFactory>()?.CreateLogger(GetType()) ?? NullLogger.Instance;
        if (!State.Active || State.Released)
            return;

        await EnsureObservationRelayAsync(State.SourceScopeActorId, ct);
        // A durable retry survives deactivation on its own; re-arming it here only covers a
        // scheduler that lost it (and is idempotent: the same callback id, one live retry).
        var pending = State.PendingWrite;
        if (pending?.Source != null)
            await ScheduleWriteRetryAsync(pending.Source, pending.Attempts, ResolveWriteRetryDelay(pending), ct);
    }

    /// <summary>
    /// Every pending write stays durably retryable: a transient (or, for a binary that recorded
    /// no kind, unspecified) failure follows the backed-off cadence; a rejected write (the store
    /// holds different bytes at this version) is retried at the capped cadence — the same bytes
    /// cannot heal it, but a later write at or above its version clears it, and until then it
    /// stays visible instead of being silently dropped.
    /// </summary>
    internal static TimeSpan ResolveWriteRetryDelay(ProjectionScopeStatusPendingWrite pending) =>
        pending.FailureKind == ProjectionScopeStatusWriteFailureKind.Rejected
            ? WriteRetryDelays[^1]
            : ResolveWriteRetryDelay(pending.Attempts);

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        // The relay is durable evidence that this materializer owns the source's status; it
        // is removed only by explicit release, never by a transient deactivation.
        if (State.Released || !State.Active)
            await RemoveObservationRelayAsync(State.SourceScopeActorId, ct);

        await base.OnDeactivateAsync(ct);
    }

    [EventHandler]
    public async Task HandleEnsureAsync(EnsureProjectionScopeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateLifecycleCommand(command.RootActorId, command.ProjectionKind, command.SessionId, command.Mode);

        if (!State.Active || State.Released ||
            !string.Equals(State.SourceScopeActorId, command.RootActorId, StringComparison.Ordinal))
        {
            await StartAsync(command.RootActorId);
        }

        await EnsureObservationRelayAsync(State.SourceScopeActorId, CancellationToken.None);
    }

    private Task StartAsync(string sourceScopeActorId) =>
        PersistDomainEventAsync(new ProjectionScopeStatusTerminalStartedEvent
        {
            SourceScopeActorId = sourceScopeActorId,
            ContractId = ContractId,
            ContractVersion = ContractVersion,
            ActivationGeneration = Math.Max(1, State.ActivationGeneration + 1),
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });

    [EventHandler]
    public async Task HandleReleaseAsync(ReleaseProjectionScopeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateLifecycleCommand(command.RootActorId, command.ProjectionKind, command.SessionId, command.Mode);
        if (command.StatusRouteEpoch > 0)
        {
            if (!IsExpectedDirectPublisher(command.RootActorId) ||
                !string.Equals(command.RootActorId, State.SourceScopeActorId, StringComparison.Ordinal) ||
                !string.Equals(command.ExpectedWriterActorId, Id, StringComparison.Ordinal) ||
                command.RequiredObservedVersion <= 0 ||
                !State.Released ||
                State.ReleasedAtObservedVersion < command.RequiredObservedVersion)
            {
                return;
            }

            await RemoveObservationRelayAsync(State.SourceScopeActorId, CancellationToken.None);
            // A status-route cutover release is confirmed only after the release is committed.
            // Terminal drain is normally committed while handling the source's BLOCKED
            // publication; a direct command that races ahead of that publication does nothing.
            await ConfirmReleasedAsync(command.RootActorId, command.StatusRouteEpoch);
            return;
        }

        await ReleaseAsync(lastObservedVersion: State.ReleasedAtObservedVersion);
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

    [EventHandler]
    public async Task HandleStatusActorSealRequestAsync(RequestProjectionScopeStatusActorSealCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Role != ProjectionScopeStatusActorRole.TerminalWriter ||
            command.RouteEpoch <= 0 ||
            string.IsNullOrWhiteSpace(command.SourceScopeActorId) ||
            !string.Equals(command.ExpectedActorId, Id, StringComparison.Ordinal) ||
            !string.Equals(command.ExpectedAgentKind, AgentKind, StringComparison.Ordinal) ||
            !IsExpectedDirectPublisher(command.SourceScopeActorId) ||
            (!string.IsNullOrWhiteSpace(State.SourceScopeActorId) &&
             !string.Equals(State.SourceScopeActorId, command.SourceScopeActorId, StringComparison.Ordinal)) ||
            !ProjectionScopeStatusActivationSealPolicy.TryCreate(
                Services.GetService<IRuntimeActorStateSchemaContextReader>(),
                ProjectionScopeStatusActorRole.TerminalWriter,
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

    private Task ConfirmReleasedAsync(string sourceScopeActorId, long routeEpoch) =>
        SendToAsync(sourceScopeActorId, new ProjectionScopeStatusWriterReleasedEvent
        {
            SourceScopeActorId = sourceScopeActorId,
            RouteEpoch = routeEpoch,
            LastObservedVersion = State.ReleasedAtObservedVersion,
            ReleasedAtUtc = Timestamp.FromDateTimeOffset(Now()),
            WriterActorId = Id,
        });

    [AllEventHandler(Priority = 50, AllowSelfHandling = true)]
    public async Task HandleObservedEnvelopeAsync(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!envelope.Route.IsObserverPublication())
            return;
        if (!StreamForwardingRules.IsForwardedEnvelopeForTarget(envelope, Id) ||
            StreamForwardingRules.IsTransitOnlyForwarding(envelope))
        {
            return;
        }

        if (!CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var sourceState) ||
            stateEvent?.EventData == null ||
            sourceState == null)
        {
            return;
        }

        var sourceScopeActorId = BuildSourceScopeActorId(sourceState);
        if (!IsAuthenticForwardedSourcePublication(envelope, stateEvent, sourceScopeActorId))
            return;

        var route = sourceState.StatusRoute;
        var warmingTerminalRoute = ProjectionScopeStatusRoutePolicy.IsWarmingTerminalRoute(
            route,
            ContractId,
            ContractVersion);
        var servedTerminalRouteBlocked =
            route?.Phase == ProjectionScopeStatusRoutePhase.Blocked &&
            ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                route,
                ContractId,
                ContractVersion);
        var activeLegacyWriterBlocked =
            State.Active &&
            !State.Released &&
            route?.Phase == ProjectionScopeStatusRoutePhase.Blocked &&
            ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route);
        if (warmingTerminalRoute || servedTerminalRouteBlocked || activeLegacyWriterBlocked)
        {
            // Historical and pre-seal WARMING/BLOCKED publications cannot acquire durable seals
            // when redelivered because their committed state image is immutable. Keep those
            // publications frozen. Once the exact route already carries its three writer seals,
            // however, missing live fleet proof is transient and must request redelivery.
            if (!HasTerminalWriterBoundPhaseBSeals(route!, sourceScopeActorId))
                return;

            if (!await HasTerminalWriterLivePhaseBProofsAsync())
            {
                throw new ProjectionScopeStatusPhaseBProofUnavailableException(
                    Id,
                    sourceScopeActorId,
                    route!.RouteEpoch,
                    route.Phase,
                    ProjectionScopeStatusActorRole.TerminalWriter);
            }
        }

        var namesThisWriter =
            warmingTerminalRoute ||
            ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(route, ContractId, ContractVersion);
        if (!State.Active || State.Released)
        {
            // The source scope writes our relay and commits the route on its own turn; its
            // first routed publication can reach us before the lifecycle command does, and a
            // re-ensured source re-asserts our relay after we released with it. Only a
            // publication whose committed route names this contract may
            // start us, it must be the source the relay was written for (the relay is addressed
            // to this actor), and a released materializer never restarts for a source that is
            // itself released.
            if (!namesThisWriter || (State.Released && sourceState.Released))
                return;

            await StartAsync(sourceScopeActorId);
        }
        else if (!string.Equals(sourceScopeActorId, State.SourceScopeActorId, StringComparison.Ordinal))
        {
            return;
        }

        if (warmingTerminalRoute)
        {
            await SendToAsync(State.SourceScopeActorId, new ProjectionScopeStatusWriterCaughtUpEvent
            {
                SourceScopeActorId = State.SourceScopeActorId,
                RouteEpoch = route!.RouteEpoch,
                ObservedVersion = stateEvent.Version,
                ObservedAtUtc = Timestamp.FromDateTimeOffset(Now()),
                WriterActorId = Id,
            });
            return;
        }

        if (ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route) &&
            ProjectionScopeStatusRoutePolicy.IsWritingPhase(route!))
        {
            // Rolled back: the legacy shadow took the route over (blocked/active). We are no
            // longer a writer for this source; this publication is the last one observed through
            // the source's relay (stream order), so the confirmation carries it as drained.
            await ReleaseAsync(stateEvent.Version);
            if (route!.Phase == ProjectionScopeStatusRoutePhase.Blocked)
                await ConfirmReleasedAsync(State.SourceScopeActorId, route.RouteEpoch);
            return;
        }

        var mayWrite =
            ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(route, ContractId, ContractVersion) ||
            // A legacy route that is still warming (rollback in flight): we remain the writer
            // until the source blocks the route.
            (ProjectionScopeStatusRoutePolicy.IsLegacyRoute(route) &&
             route!.Phase == ProjectionScopeStatusRoutePhase.Warming);
        if (!mayWrite)
            return;

        if (!ProjectionScopeStatusRoutePolicy.IsTerminalOutcome(stateEvent.EventData))
            return;

        var source = new ProjectionSourceCoordinate
        {
            ActorId = State.SourceScopeActorId,
            StateVersion = stateEvent.Version,
            EventId = stateEvent.EventId ?? string.Empty,
        };
        await WriteStatusAsync(envelope, sourceState, stateEvent, source);
        await ReleaseWithSourceAsync(sourceState, stateEvent.Version);
    }

    private async Task<bool> HasTerminalQuiescenceReceiptAsync()
    {
        var reader = Services.GetService<IRuntimeFleetCapabilityQuiescenceReader>();
        if (reader == null)
            return false;

        return await RuntimeFleetCapabilityAdmissionValidation.GetQuiescenceReceiptAsync(
                   RuntimeFleetCapability.ProjectionScopeStatusTerminalV2,
                   RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceV1,
                   RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalQuiescenceReaderVersion,
                   reader,
                   CancellationToken.None) != null;
    }

    private bool HasTerminalWriterBoundPhaseBSeals(
        ProjectionScopeStatusRoute route,
        string sourceScopeActorId)
    {
        var reader = Services.GetService<IRuntimeActorStateSchemaContextReader>();
        return ProjectionScopeStatusActivationSealPolicy.TryCreate(
                   reader,
                   ProjectionScopeStatusActorRole.TerminalWriter,
                   Id,
                   AgentKind,
                   out var currentWriterSeal) &&
               ProjectionScopeStatusActivationSealPolicy.RouteHasAllRequiredWriterSeals(
                   route,
                   sourceScopeActorId,
                   ProjectionScopeStatusRoutes.BuildLegacyActorId(sourceScopeActorId),
                   ProjectionScopeStatusRoutes.BuildTerminalActorId(sourceScopeActorId),
                   currentWriterSeal);
    }

    private async Task<bool> HasTerminalWriterLivePhaseBProofsAsync() =>
        await HasTerminalQuiescenceReceiptAsync() &&
        await ProjectionScopeStatusActivationSealPolicy.ReadFreshAdmissionAsync(
            Services,
            CancellationToken.None) != null;

    /// <summary>
    /// A released and detached source publishes nothing further, so this materializer
    /// releases with it; but only once no write is pending, otherwise the source's final
    /// status document could never be written (release clears the pending write).
    /// </summary>
    private Task ReleaseWithSourceAsync(ProjectionScopeState sourceState, long observedVersion) =>
        sourceState.Released && !sourceState.ObservationAttached && State.PendingWrite == null
            ? ReleaseAsync(lastObservedVersion: observedVersion)
            : Task.CompletedTask;

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleRetryWriteAsync(RetryProjectionScopeStatusWriteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var pending = State.PendingWrite;
        if (!State.Active || State.Released || pending?.Source == null || pending.Envelope == null)
            return;
        if (command.ExpectedSource == null || !SameSource(command.ExpectedSource, pending.Source))
            return;
        // Same-source attempt fence: a delayed callback of an earlier attempt (the durable retry
        // state already advanced past it) must not overwrite the retry state with a lower
        // attempt and a shorter backoff.
        if (command.Attempt != pending.Attempts)
            return;

        if (!CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                pending.Envelope,
                out _,
                out var stateEvent,
                out var sourceState) ||
            stateEvent == null ||
            sourceState == null)
        {
            return;
        }

        await WriteStatusAsync(pending.Envelope, sourceState, stateEvent, pending.Source, command.Attempt, redeliverable: false);
        await ReleaseWithSourceAsync(sourceState, stateEvent.Version);
    }

    protected override ProjectionScopeStatusTerminalState TransitionState(
        ProjectionScopeStatusTerminalState current,
        Google.Protobuf.IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ProjectionScopeStatusTerminalStartedEvent>(ProjectionScopeStatusTerminalStateApplier.ApplyStarted)
            .On<ProjectionScopeStatusTerminalReleasedEvent>(ProjectionScopeStatusTerminalStateApplier.ApplyReleased)
            .On<ProjectionScopeStatusWriteDeferredEvent>(ProjectionScopeStatusTerminalStateApplier.ApplyWriteDeferred)
            .On<ProjectionScopeStatusWriteRecoveredEvent>(ProjectionScopeStatusTerminalStateApplier.ApplyWriteRecovered)
            .On<ProjectionScopeStatusWriteStalledEvent>(ProjectionScopeStatusTerminalStateApplier.ApplyWriteStalled)
            .OrCurrent();

    /// <summary>
    /// Writes the status document for one terminal source outcome. A store exception defers the
    /// write durably (the observation is acknowledged because the retry is actor-owned). A
    /// Conflict/Gap disposition (the store holds different bytes at this version, or a gap was
    /// detected) never advances delivery: on the observed path the observation fails so the
    /// provider redelivers it without advancing its checkpoint; on the durable retry path the
    /// pending write stays durably retryable at the capped cadence.
    /// </summary>
    private async Task WriteStatusAsync(
        EventEnvelope envelope,
        ProjectionScopeState sourceState,
        StateEvent stateEvent,
        ProjectionSourceCoordinate source,
        int attempt = 0,
        bool redeliverable = true)
    {
        var clock = Services.GetService<IProjectionClock>();
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(
            envelope,
            clock?.UtcNow ?? Now());
        var document = ProjectionScopeStatusDocumentMapper.Map(sourceState, stateEvent, updatedAt);
        var dispatcher = Services.GetRequiredService<IProjectionWriteDispatcher<ProjectionScopeStatusDocument>>();

        ProjectionWriteResult result;
        try
        {
            result = await dispatcher.UpsertAsync(document, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The observed envelope is acknowledged only because the retry is now durable and
            // actor-owned; a later terminal write at a higher version supersedes it.
            await DeferWriteAsync(
                envelope,
                source,
                attempt,
                ProjectionScopeStatusWriteFailureKind.Transient,
                exception.GetType().Name);
            _logger.LogWarning(
                exception,
                "Terminal status write failed and was deferred. actorId={ActorId} source={SourceActorId} version={StateVersion} attempt={Attempt}",
                Id,
                source.ActorId,
                source.StateVersion,
                attempt);
            return;
        }

        if (result.Disposition is ProjectionWriteDisposition.Conflict or ProjectionWriteDisposition.Gap)
        {
            _logger.LogError(
                "Terminal status write was rejected as {Disposition}; delivery is not advanced. actorId={ActorId} source={SourceActorId} version={StateVersion} attempt={Attempt}",
                result.Disposition,
                Id,
                source.ActorId,
                source.StateVersion,
                attempt);
            if (redeliverable)
            {
                // Nothing is persisted for this observation: it fails and is redelivered by the
                // provider without advancing the target checkpoint.
                await PublishWriteFailureAlertAsync(source, attempt + 1, result.Disposition.ToString(), Now());
                throw new ProjectionScopeStatusWriteRejectedException(Id, source, result.Disposition);
            }

            await DeferWriteAsync(
                envelope,
                source,
                attempt,
                ProjectionScopeStatusWriteFailureKind.Rejected,
                result.Disposition.ToString());
            return;
        }

        var pending = State.PendingWrite?.Source;
        if (pending != null && pending.StateVersion <= source.StateVersion)
        {
            await PersistDomainEventAsync(new ProjectionScopeStatusWriteRecoveredEvent
            {
                Source = source.Clone(),
                OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
            });
        }
    }

    private async Task DeferWriteAsync(
        EventEnvelope envelope,
        ProjectionSourceCoordinate source,
        int attempt,
        ProjectionScopeStatusWriteFailureKind failureKind,
        string reason)
    {
        var existing = State.PendingWrite;
        if (existing?.Source != null && existing.Source.StateVersion > source.StateVersion)
            return; // keep the newer pending write; this one is superseded

        var attempts = attempt + 1;
        var now = Now();
        var stalled = failureKind == ProjectionScopeStatusWriteFailureKind.Transient &&
                      attempts >= StalledAttemptThreshold;
        var sameSourcePending = existing?.Source != null && SameSource(existing.Source, source);
        var alreadyStalled = sameSourcePending && existing!.Stalled;
        var pending = new ProjectionScopeStatusPendingWrite
        {
            Source = source.Clone(),
            Envelope = envelope.Clone(),
            Attempts = attempts,
            LastError = reason,
            DeferredAtUtc = Timestamp.FromDateTimeOffset(now),
            FailureKind = failureKind,
            Stalled = stalled || alreadyStalled,
        };
        var retryDelay = ResolveWriteRetryDelay(pending);
        pending.NextRetryAtUtc = Timestamp.FromDateTimeOffset(now + retryDelay);
        await PersistDomainEventAsync(new ProjectionScopeStatusWriteDeferredEvent
        {
            Pending = pending,
            OccurredAtUtc = Timestamp.FromDateTimeOffset(now),
        });

        if (failureKind == ProjectionScopeStatusWriteFailureKind.Rejected)
        {
            // Alert once per rejected source; the capped-cadence retry keeps the fact durable.
            var alreadyRejected = sameSourcePending &&
                                  existing!.FailureKind == ProjectionScopeStatusWriteFailureKind.Rejected;
            if (!alreadyRejected)
                await PublishWriteFailureAlertAsync(source, attempts, reason, now);
            await ScheduleWriteRetryAsync(source, attempts, retryDelay, CancellationToken.None);
            return;
        }

        if (stalled && !alreadyStalled)
        {
            await PersistDomainEventAsync(new ProjectionScopeStatusWriteStalledEvent
            {
                Source = source.Clone(),
                Attempts = attempts,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(now),
            });
            _logger.LogError(
                "Terminal status write is stalled after {Attempts} attempts; retries continue at the capped cadence. actorId={ActorId} source={SourceActorId} version={StateVersion} lastError={LastError}",
                attempts,
                Id,
                source.ActorId,
                source.StateVersion,
                reason);
            await PublishWriteFailureAlertAsync(source, attempts, reason, now);
        }

        await ScheduleWriteRetryAsync(source, attempts, retryDelay, CancellationToken.None);
    }

    internal static TimeSpan ResolveWriteRetryDelay(int attempts) =>
        WriteRetryDelays[Math.Clamp(attempts - 1, 0, WriteRetryDelays.Length - 1)];

    /// <summary>
    /// Durable, backed-off self continuation through the callback scheduler: it fires after
    /// deactivation and across silo restarts, so a recovered store is picked up without any
    /// new source event, manual ensure or actor deactivation.
    /// </summary>
    private async Task ScheduleWriteRetryAsync(
        ProjectionSourceCoordinate source,
        int attempts,
        TimeSpan delay,
        CancellationToken ct)
    {
        if (Services.GetService<IActorRuntimeCallbackScheduler>() == null)
        {
            _logger.LogWarning(
                "Terminal status write retry cannot be scheduled: no durable callback scheduler. actorId={ActorId} source={SourceActorId} version={StateVersion}",
                Id,
                source.ActorId,
                source.StateVersion);
            return;
        }

        _ = await ScheduleSelfDurableTimeoutAsync(
            WriteRetryCallbackId,
            delay,
            new RetryProjectionScopeStatusWriteCommand
            {
                ExpectedSource = source.Clone(),
                Attempt = attempts,
            },
            ct: ct);
    }

    private async Task PublishWriteFailureAlertAsync(
        ProjectionSourceCoordinate source,
        int attempts,
        string reason,
        DateTimeOffset now)
    {
        var sink = Services.GetService<IProjectionFailureAlertSink>();
        if (sink == null)
            return;

        try
        {
            await sink.PublishAsync(new ProjectionFailureAlert(
                ProjectionFailureAlertKind.FailureRecorded,
                new ProjectionRuntimeScopeKey(
                    State.SourceScopeActorId,
                    ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                    ProjectionRuntimeMode.DurableMaterialization),
                FailureId: $"{source.ActorId}:{source.StateVersion}:{source.EventId}",
                Stage: "terminal-status-write",
                EventId: source.EventId,
                EventType: nameof(ProjectionScopeStatusDocument),
                SourceVersion: source.StateVersion,
                Reason: reason,
                UnresolvedFailureCount: 1,
                DroppedCount: 0,
                DroppedFailureIds: [],
                DiagnosticDroppedTotal: 0,
                OccurredAt: now));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Terminal status write alert could not be published. actorId={ActorId}", Id);
        }
    }

    private async Task ReleaseAsync(long lastObservedVersion)
    {
        if (!State.Active)
            return;

        if (!State.Released)
        {
            await PersistDomainEventAsync(new ProjectionScopeStatusTerminalReleasedEvent
            {
                LastObservedVersion = lastObservedVersion,
                OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
            });
        }

        // The release watermark is authoritative. Relay cleanup is derived and idempotent, so a
        // crash or store failure can never remove the only path that can prove the drain.
        await RemoveObservationRelayAsync(State.SourceScopeActorId, CancellationToken.None);
    }

    private static string BuildSourceScopeActorId(ProjectionScopeState sourceState) =>
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            sourceState.RootActorId,
            sourceState.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(sourceState.Mode),
            sourceState.SessionId));

    private static bool IsAuthenticForwardedSourcePublication(
        EventEnvelope envelope,
        StateEvent stateEvent,
        string sourceScopeActorId) =>
        !string.IsNullOrWhiteSpace(sourceScopeActorId) &&
        string.Equals(envelope.Route?.PublisherActorId, sourceScopeActorId, StringComparison.Ordinal) &&
        string.Equals(
            StreamForwardingEnvelopeState.GetSourceStreamId(envelope),
            sourceScopeActorId,
            StringComparison.Ordinal) &&
        string.Equals(stateEvent.AgentId, sourceScopeActorId, StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(envelope.Runtime?.SourceActorId) ||
         string.Equals(envelope.Runtime.SourceActorId, sourceScopeActorId, StringComparison.Ordinal));

    private Task EnsureObservationRelayAsync(string sourceScopeActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceScopeActorId))
            return Task.CompletedTask;

        return Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(sourceScopeActorId)
            .UpsertRelayAsync(BuildObservationRelayBinding(sourceScopeActorId), ct);
    }

    private Task RemoveObservationRelayAsync(string sourceScopeActorId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceScopeActorId))
            return Task.CompletedTask;

        return Services
            .GetRequiredService<IStreamProvider>()
            .GetStream(sourceScopeActorId)
            .RemoveRelayAsync(Id, ct);
    }

    private StreamForwardingBinding BuildObservationRelayBinding(string sourceScopeActorId)
    {
        var registry = Services.GetRequiredService<IAgentKindRegistry>();
        if (!registry.TryGetKindForAgentType(GetType(), out var targetActorKind))
        {
            throw new InvalidOperationException(
                $"Terminal status materializer type {GetType().FullName} is not registered with a primary agent kind.");
        }

        return ProjectionScopeObservationRelayBinding.Create(
            sourceScopeActorId,
            Id,
            targetActorKind,
            State.ActivationGeneration);
    }

    private static void ValidateLifecycleCommand(
        string? sourceScopeActorId,
        string? projectionKind,
        string? sessionId,
        ProjectionScopeMode mode)
    {
        if (string.IsNullOrWhiteSpace(sourceScopeActorId) ||
            !string.Equals(
                projectionKind,
                ProjectionScopeStatusTerminalMaterializationContext.ProjectionKindValue,
                StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(sessionId) ||
            mode != ProjectionScopeMode.DurableMaterialization)
        {
            throw new InvalidOperationException(
                "Terminal status materializer received a mismatched lifecycle command.");
        }
    }

    private static bool SameSource(ProjectionSourceCoordinate left, ProjectionSourceCoordinate right) =>
        left.StateVersion == right.StateVersion &&
        string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) &&
        string.Equals(left.EventId, right.EventId, StringComparison.Ordinal);

    private DateTimeOffset Now() =>
        (Services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
}

/// <summary>
/// The terminal status write for an observed source outcome was rejected (Conflict/Gap): the
/// observation fails so the provider redelivers it without advancing its checkpoint. Nothing
/// about the observation is persisted by the materializer.
/// </summary>
public sealed class ProjectionScopeStatusWriteRejectedException(
    string materializerActorId,
    ProjectionSourceCoordinate source,
    ProjectionWriteDisposition disposition)
    : InvalidOperationException(
        $"Terminal status materializer '{materializerActorId}' could not apply the status document for source '{source.ActorId}' version {source.StateVersion} ({disposition}); the observation is redelivered.")
    , IRuntimeEnvelopeRetryableException
{
    public string MaterializerActorId { get; } = materializerActorId;

    public ProjectionSourceCoordinate Source { get; } = source;

    public ProjectionWriteDisposition Disposition { get; } = disposition;
}

internal static class ProjectionScopeStatusTerminalStateApplier
{
    public static ProjectionScopeStatusTerminalState ApplyStarted(
        ProjectionScopeStatusTerminalState current,
        ProjectionScopeStatusTerminalStartedEvent evt)
    {
        var next = current.Clone();
        next.SourceScopeActorId = evt.SourceScopeActorId;
        next.ContractId = evt.ContractId;
        next.ContractVersion = evt.ContractVersion;
        next.Active = true;
        next.Released = false;
        next.ActivationGeneration = evt.ActivationGeneration;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeStatusTerminalState ApplyReleased(
        ProjectionScopeStatusTerminalState current,
        ProjectionScopeStatusTerminalReleasedEvent evt)
    {
        var next = current.Clone();
        next.Released = true;
        next.PendingWrite = null;
        next.ReleasedAtObservedVersion = Math.Max(current.ReleasedAtObservedVersion, evt.LastObservedVersion);
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeStatusTerminalState ApplyWriteDeferred(
        ProjectionScopeStatusTerminalState current,
        ProjectionScopeStatusWriteDeferredEvent evt)
    {
        var next = current.Clone();
        next.PendingWrite = evt.Pending?.Clone();
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeStatusTerminalState ApplyWriteStalled(
        ProjectionScopeStatusTerminalState current,
        ProjectionScopeStatusWriteStalledEvent evt)
    {
        var next = current.Clone();
        if (next.PendingWrite?.Source != null &&
            evt.Source != null &&
            next.PendingWrite.Source.StateVersion == evt.Source.StateVersion)
        {
            next.PendingWrite.Stalled = true;
        }

        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeStatusTerminalState ApplyWriteRecovered(
        ProjectionScopeStatusTerminalState current,
        ProjectionScopeStatusWriteRecoveredEvent evt)
    {
        var next = current.Clone();
        var pending = next.PendingWrite?.Source;
        if (pending != null && evt.Source != null && pending.StateVersion <= evt.Source.StateVersion)
            next.PendingWrite = null;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }
}
