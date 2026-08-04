using System.Diagnostics;
using Aevatar.CQRS.Projection.Core.Observability;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
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
    private ILogger _logger = NullLogger.Instance;
    private ProjectionScopeFailureTracker? _failureTracker;
    private bool _isReplayingFailure;

    protected abstract ProjectionRuntimeMode RuntimeMode { get; }

    protected override Task OnActivateAsync(CancellationToken ct)
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
            return Task.CompletedTask;

        return EnsureObservationRelayAsync(State.RootActorId, ct);
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        await RemoveObservationRelayAsync(State.RootActorId, ct);
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
            });
        }

        await EnsureObservationRelayAsync(command.RootActorId, CancellationToken.None);
        if (!State.ObservationAttached)
        {
            await PersistDomainEventAsync(new ProjectionObservationAttachmentUpdatedEvent
            {
                Attached = true,
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }
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

    [EventHandler]
    public async Task HandleReplayAsync(ReplayProjectionFailuresCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!State.Active || State.Released || State.Failures.Count == 0)
            return;

        await _failureTracker!.ReplayAsync(
            State,
            command.MaxItems,
            (envelope, ct) => DispatchObservationAsync(envelope, ct, bypassSuccessfulVersionFence: true));
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
            if (ProjectionObservationFailurePolicy.ShouldPropagate(ex))
            {
                // ShouldPropagate currently only returns true for OCC (direct or
                // wrapped). Discard stale pending events so the grain can deactivate
                // cleanly; state will rebuild from the event store on next activation.
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
            .On<ProjectionObservationAttachmentUpdatedEvent>(ProjectionScopeStateApplier.ApplyAttachmentUpdated)
            .On<ProjectionScopeReleasedEvent>(ProjectionScopeStateApplier.ApplyReleased)
            .On<ProjectionScopeEnvelopeReceivedEvent>(ProjectionScopeStateApplier.ApplyEnvelopeReceived)
            .On<ProjectionScopeEnvelopeAttemptedEvent>(ProjectionScopeStateApplier.ApplyEnvelopeAttempted)
            .On<ProjectionScopeWatermarkAdvancedEvent>(ProjectionScopeStateApplier.ApplyWatermarkAdvanced)
            .On<ProjectionScopeDispatchFailedEvent>(ProjectionScopeStateApplier.ApplyDispatchFailed)
            .On<ProjectionScopeFailureReplayedEvent>(ProjectionScopeStateApplier.ApplyFailureReplayed)
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

    private async Task<ProjectionScopeDispatchResult> DispatchObservationAsync(
        EventEnvelope envelope,
        CancellationToken ct,
        bool bypassSuccessfulVersionFence = false)
    {
        var context = ResolveScopeContext();
        var sourceActorId = ResolveSourceActorId(envelope);
        var eventKind = ResolveEventKind(envelope);
        if (!bypassSuccessfulVersionFence)
        {
            await PersistDomainEventAsync(new ProjectionScopeEnvelopeReceivedEvent
            {
                OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                EventKind = eventKind,
            });
            ProjectionProcessingMetrics.RecordReceived(State.ProjectionKind, eventKind);
        }

        if (!bypassSuccessfulVersionFence && IsAlreadyProjected(sourceActorId, envelope))
            return ProjectionScopeDispatchResult.Skip(envelope.Payload?.TypeUrl ?? string.Empty);

        var sourceVersion = ResolveSourceVersion(envelope);
        await PersistDomainEventAsync(new ProjectionScopeEnvelopeAttemptedEvent
        {
            HighestSeenVersion = sourceVersion,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            SourceActorId = sourceActorId,
            ObservedEnvelope = BuildObservedEnvelopeMetadata(envelope),
            EventKind = eventKind,
        });
        ProjectionProcessingMetrics.RecordAttempted(State.ProjectionKind, eventKind);

        var startedAt = Stopwatch.GetTimestamp();
        var previousReplayState = _isReplayingFailure;
        _isReplayingFailure = bypassSuccessfulVersionFence;
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

        await PersistDomainEventAsync(new ProjectionScopeWatermarkAdvancedEvent
        {
            LastSuccessfulVersion = result.SuccessfulVersion,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            SourceActorId = sourceActorId,
        });
        ProjectionProcessingMetrics.RecordSucceeded(
            State.ProjectionKind,
            result.EventType,
            Stopwatch.GetElapsedTime(startedAt));
        return result;
    }

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
        return ProjectionScopeObservationRelayBinding.Create(rootActorId, Id);
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
