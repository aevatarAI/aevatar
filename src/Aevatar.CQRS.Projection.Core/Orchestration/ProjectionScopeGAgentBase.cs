using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public abstract class ProjectionScopeGAgentBase<TContext>
    : GAgentBase<ProjectionScopeState>
    where TContext : class, IProjectionMaterializationContext
{
    private IAsyncDisposable? _observationSubscription;
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private ILogger _logger = NullLogger.Instance;

    protected abstract ProjectionRuntimeMode RuntimeMode { get; }

    protected override Task OnActivateAsync(CancellationToken ct)
    {
        _logger = Services.GetService<ILoggerFactory>()?.CreateLogger(GetType()) ?? NullLogger.Instance;

        if (!State.Active || State.Released)
            return Task.CompletedTask;

        return EnsureObservationAttachedAsync(persistState: false, ct);
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        await DetachObservationAsync(ct);
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

        await EnsureObservationAttachedAsync(persistState: !State.ObservationAttached, CancellationToken.None);
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

        await DetachObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleReplayAsync(ReplayProjectionFailuresCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!State.Active || State.Released || State.Failures.Count == 0)
            return;

        var maxItems = Math.Max(1, command.MaxItems);
        var failures = State.Failures.Take(maxItems).ToList();
        foreach (var failure in failures)
        {
            var envelope = failure.Envelope;
            if (envelope == null)
                continue;

            try
            {
                var result = await DispatchObservationAsync(envelope, CancellationToken.None);
                if (result.Handled)
                {
                    await PersistDomainEventAsync(new ProjectionScopeFailureReplayedEvent
                    {
                        FailureId = failure.FailureId,
                        Succeeded = true,
                        Reason = string.Empty,
                        OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                    });
                }
            }
            catch (Exception ex)
            {
                await PersistDomainEventAsync(new ProjectionScopeFailureReplayedEvent
                {
                    FailureId = failure.FailureId,
                    Succeeded = false,
                    Reason = ex.Message ?? ex.GetType().Name,
                    OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                });
            }
        }
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleObservationAsync(ProjectionObservationArrivedSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (!State.Active || State.Released || signal.Envelope == null)
            return;

        try
        {
            await DispatchObservationAsync(signal.Envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
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
            .On<ProjectionScopeStartedEvent>(ApplyStarted)
            .On<ProjectionObservationAttachmentUpdatedEvent>(ApplyAttachmentUpdated)
            .On<ProjectionScopeReleasedEvent>(ApplyReleased)
            .On<ProjectionScopeWatermarkAdvancedEvent>(ApplyWatermarkAdvanced)
            .On<ProjectionScopeDispatchFailedEvent>(ApplyDispatchFailed)
            .On<ProjectionScopeFailureReplayedEvent>(ApplyFailureReplayed)
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
        CancellationToken ct)
    {
        var context = ResolveScopeContext();
        var result = await ProcessObservationCoreAsync(context, envelope, ct);
        if (!result.Handled)
            return result;

        await PersistDomainEventAsync(new ProjectionScopeWatermarkAdvancedEvent
        {
            LastObservedVersion = result.LastObservedVersion,
            LastSuccessfulVersion = result.LastSuccessfulVersion,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        return result;
    }

    private TContext ResolveScopeContext()
    {
        var factory = Services.GetRequiredService<IProjectionScopeContextFactory<TContext>>();
        return factory.Create(BuildScopeKey());
    }

    private async Task EnsureObservationAttachedAsync(bool persistState, CancellationToken ct)
    {
        await _subscriptionGate.WaitAsync(ct);
        try
        {
            if (_observationSubscription != null)
                return;

            var streamProvider = Services.GetRequiredService<IStreamProvider>();
            var stream = streamProvider.GetStream(State.RootActorId);
            _observationSubscription = await stream.SubscribeAsync<EventEnvelope>(
                envelope => ForwardObservationAsync(envelope),
                ct);

            if (persistState)
            {
                await PersistDomainEventAsync(new ProjectionObservationAttachmentUpdatedEvent
                {
                    Attached = true,
                    OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
                });
            }
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }

    private async Task DetachObservationAsync(CancellationToken ct)
    {
        await _subscriptionGate.WaitAsync(ct);
        try
        {
            if (_observationSubscription == null)
                return;

            await _observationSubscription.DisposeAsync();
            _observationSubscription = null;
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }

    private async Task ForwardObservationAsync(EventEnvelope envelope)
    {
        try
        {
            await PublishAsync(
                new ProjectionObservationArrivedSignal
                {
                    Envelope = envelope.Clone(),
                },
                TopologyAudience.Self,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Projection scope failed to forward observation to self. actorId={ActorId}",
                Id);
        }
    }

    private static ProjectionScopeState ApplyStarted(ProjectionScopeState current, ProjectionScopeStartedEvent evt)
    {
        var next = current.Clone();
        next.RootActorId = evt.RootActorId;
        next.ProjectionKind = evt.ProjectionKind;
        next.SessionId = evt.SessionId;
        next.Mode = evt.Mode;
        next.Active = true;
        next.Released = false;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    private static ProjectionScopeState ApplyAttachmentUpdated(
        ProjectionScopeState current,
        ProjectionObservationAttachmentUpdatedEvent evt)
    {
        var next = current.Clone();
        next.ObservationAttached = evt.Attached;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    private static ProjectionScopeState ApplyReleased(ProjectionScopeState current, ProjectionScopeReleasedEvent evt)
    {
        var next = current.Clone();
        next.Released = true;
        next.ObservationAttached = false;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    private static ProjectionScopeState ApplyWatermarkAdvanced(
        ProjectionScopeState current,
        ProjectionScopeWatermarkAdvancedEvent evt)
    {
        var next = current.Clone();
        next.LastObservedVersion = Math.Max(current.LastObservedVersion, evt.LastObservedVersion);
        next.LastSuccessfulVersion = Math.Max(current.LastSuccessfulVersion, evt.LastSuccessfulVersion);
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    private static ProjectionScopeState ApplyDispatchFailed(
        ProjectionScopeState current,
        ProjectionScopeDispatchFailedEvent evt)
    {
        var next = current.Clone();
        next.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = evt.FailureId,
            Stage = evt.Stage,
            EventId = evt.EventId,
            EventType = evt.EventType,
            SourceVersion = evt.SourceVersion,
            Reason = evt.Reason,
            Envelope = evt.Envelope?.Clone(),
            Attempts = 0,
            OccurredAtUtc = evt.OccurredAtUtc?.Clone(),
        });
        ProjectionFailureRetentionPolicy.Trim(next.Failures);
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    private static ProjectionScopeState ApplyFailureReplayed(
        ProjectionScopeState current,
        ProjectionScopeFailureReplayedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Failures.FirstOrDefault(x => string.Equals(x.FailureId, evt.FailureId, StringComparison.Ordinal));
        if (existing == null)
            return next;

        if (evt.Succeeded)
        {
            next.Failures.Remove(existing);
        }
        else
        {
            existing.Attempts += 1;
            existing.Reason = evt.Reason ?? existing.Reason;
        }

        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    protected async ValueTask RecordDispatchFailureAsync(
        string stage,
        string eventId,
        string eventType,
        long sourceVersion,
        string reason,
        EventEnvelope envelope)
    {
        var failureId = Guid.NewGuid().ToString("N");
        var occurredAt = DateTimeOffset.UtcNow;
        var failureCount = Math.Min(
            ProjectionFailureRetentionPolicy.DefaultMaxRetainedFailures,
            State.Failures.Count + 1);

        await PersistDomainEventAsync(new ProjectionScopeDispatchFailedEvent
        {
            FailureId = failureId,
            Stage = stage,
            EventId = eventId,
            EventType = eventType,
            SourceVersion = sourceVersion,
            Reason = reason,
            Envelope = envelope.Clone(),
            OccurredAtUtc = Timestamp.FromDateTime(occurredAt.UtcDateTime),
        });

        var alertSink = Services.GetService<IProjectionFailureAlertSink>();
        if (alertSink == null)
            return;

        try
        {
            await alertSink.PublishAsync(
                new ProjectionFailureAlert(
                    BuildScopeKey(),
                    failureId,
                    stage,
                    eventId,
                    eventType,
                    sourceVersion,
                    reason,
                    failureCount,
                    occurredAt),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Projection failure alert publishing failed. actorId={ActorId} projectionKind={ProjectionKind} sessionId={SessionId}",
                Id,
                State.ProjectionKind,
                State.SessionId);
        }
    }
}

public readonly record struct ProjectionScopeDispatchResult(
    bool Handled,
    long LastObservedVersion,
    long LastSuccessfulVersion,
    string EventType)
{
    public static ProjectionScopeDispatchResult Skip(string eventType = "") =>
        new(false, 0, 0, eventType);

    public static ProjectionScopeDispatchResult Success(
        long observedVersion,
        long successfulVersion,
        string eventType) =>
        new(true, observedVersion, successfulVersion, eventType);
}
