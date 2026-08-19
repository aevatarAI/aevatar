using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
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
[GAgent(AgentKind)]
public sealed class ProjectionScopeStatusGAgent
    : GAgentBase<ProjectionScopeStatusTerminalState>
{
    public const string AgentKind = "projection.scope-status-terminal";
    public const string ContractId = RuntimeFleetCapabilityContracts.ProjectionScopeStatusTerminalV1;
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
        if (pending?.Source != null &&
            pending.FailureKind == ProjectionScopeStatusWriteFailureKind.Transient)
        {
            await ScheduleWriteRetryAsync(pending.Source, pending.Attempts, ct);
        }
    }

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
        await ReleaseAsync();
    }

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
        if (!State.Active || State.Released)
        {
            // The source scope writes our relay and commits the route on its own turn; its
            // first routed publication can reach us before the lifecycle command does, and a
            // re-ensured source re-asserts our relay after we released with it. Only a
            // publication whose committed route names this contract may start us, it must be
            // the source the relay was written for (the relay is addressed to this actor), and
            // a released materializer never restarts for a source that is itself released.
            if (!ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                    sourceState.StatusRoute,
                    ContractId,
                    ContractVersion) ||
                (State.Released && sourceState.Released))
            {
                return;
            }

            await StartAsync(sourceScopeActorId);
        }
        else if (!string.Equals(sourceScopeActorId, State.SourceScopeActorId, StringComparison.Ordinal))
        {
            return;
        }

        if (!ProjectionScopeStatusRoutePolicy.IsActiveTerminalRoute(
                sourceState.StatusRoute,
                ContractId,
                ContractVersion))
        {
            // Warm phase or a different route: observe only. The legacy shadow (or the route
            // named by the source) is the authoritative writer until the source flips to us.
            return;
        }

        if (!ProjectionScopeStatusRoutePolicy.IsTerminalOutcome(stateEvent.EventData))
            return;

        var source = new ProjectionSourceCoordinate
        {
            ActorId = State.SourceScopeActorId,
            StateVersion = stateEvent.Version,
            EventId = stateEvent.EventId ?? string.Empty,
        };
        await WriteStatusAsync(envelope, sourceState, stateEvent, source);
        await ReleaseWithSourceAsync(sourceState);
    }

    /// <summary>
    /// A released and detached source publishes nothing further, so this materializer
    /// releases with it; but only once no write is pending, otherwise the source's final
    /// status document could never be written (release clears the pending write).
    /// </summary>
    private Task ReleaseWithSourceAsync(ProjectionScopeState sourceState) =>
        sourceState.Released && !sourceState.ObservationAttached && State.PendingWrite == null
            ? ReleaseAsync()
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
        if (pending.FailureKind == ProjectionScopeStatusWriteFailureKind.Rejected)
            return; // explicit failure state: same bytes cannot heal; a higher-version write supersedes it

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

        await WriteStatusAsync(pending.Envelope, sourceState, stateEvent, pending.Source, command.Attempt);
        await ReleaseWithSourceAsync(sourceState);
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

    private async Task WriteStatusAsync(
        EventEnvelope envelope,
        ProjectionScopeState sourceState,
        StateEvent stateEvent,
        ProjectionSourceCoordinate source,
        int attempt = 0)
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
            // Another writer committed different bytes at this version. Recording the fact keeps
            // it visible; retrying the same bytes cannot heal it, the next terminal outcome will.
            await DeferWriteAsync(
                envelope,
                source,
                attempt,
                ProjectionScopeStatusWriteFailureKind.Rejected,
                result.Disposition.ToString());
            _logger.LogError(
                "Terminal status write was rejected as {Disposition}. actorId={ActorId} source={SourceActorId} version={StateVersion}",
                result.Disposition,
                Id,
                source.ActorId,
                source.StateVersion);
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
        var alreadyStalled = existing?.Source != null &&
                             SameSource(existing.Source, source) &&
                             existing.Stalled;
        var nextRetryAt = failureKind == ProjectionScopeStatusWriteFailureKind.Transient
            ? now + ResolveWriteRetryDelay(attempts)
            : (DateTimeOffset?)null;
        await PersistDomainEventAsync(new ProjectionScopeStatusWriteDeferredEvent
        {
            Pending = new ProjectionScopeStatusPendingWrite
            {
                Source = source.Clone(),
                Envelope = envelope.Clone(),
                Attempts = attempts,
                LastError = reason,
                DeferredAtUtc = Timestamp.FromDateTimeOffset(now),
                FailureKind = failureKind,
                Stalled = stalled || alreadyStalled,
                NextRetryAtUtc = nextRetryAt == null ? null : Timestamp.FromDateTimeOffset(nextRetryAt.Value),
            },
            OccurredAtUtc = Timestamp.FromDateTimeOffset(now),
        });

        if (failureKind == ProjectionScopeStatusWriteFailureKind.Rejected)
        {
            await PublishWriteFailureAlertAsync(source, attempts, reason, now);
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

        await ScheduleWriteRetryAsync(source, attempts, CancellationToken.None);
    }

    internal static TimeSpan ResolveWriteRetryDelay(int attempts) =>
        WriteRetryDelays[Math.Clamp(attempts - 1, 0, WriteRetryDelays.Length - 1)];

    /// <summary>
    /// Durable, backed-off self continuation through the callback scheduler: it fires after
    /// deactivation and across silo restarts, so a recovered store is picked up without any
    /// new source event, manual ensure or actor deactivation.
    /// </summary>
    private async Task ScheduleWriteRetryAsync(ProjectionSourceCoordinate source, int attempts, CancellationToken ct)
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
            ResolveWriteRetryDelay(attempts),
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

    private async Task ReleaseAsync()
    {
        if (!State.Active || State.Released)
            return;

        // Remove the durable evidence first so a failed release commit can only cause a later
        // cold ensure, never a warm hit against a released materializer.
        await RemoveObservationRelayAsync(State.SourceScopeActorId, CancellationToken.None);
        await PersistDomainEventAsync(new ProjectionScopeStatusTerminalReleasedEvent
        {
            OccurredAtUtc = Timestamp.FromDateTimeOffset(Now()),
        });
    }

    private static string BuildSourceScopeActorId(ProjectionScopeState sourceState) =>
        ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            sourceState.RootActorId,
            sourceState.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(sourceState.Mode),
            sourceState.SessionId));

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
