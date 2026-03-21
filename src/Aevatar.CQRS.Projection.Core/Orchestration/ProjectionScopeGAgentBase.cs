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
    private readonly ProjectionObservationSubscriber _subscriber = new();
    private ILogger _logger = NullLogger.Instance;
    private ProjectionScopeFailureTracker? _failureTracker;

    protected abstract ProjectionRuntimeMode RuntimeMode { get; }

    protected override Task OnActivateAsync(CancellationToken ct)
    {
        _logger = Services.GetService<ILoggerFactory>()?.CreateLogger(GetType()) ?? NullLogger.Instance;
        _failureTracker = new ProjectionScopeFailureTracker(
            evt => PersistDomainEventAsync(evt),
            () => Services.GetService<IProjectionFailureAlertSink>(),
            BuildScopeKey,
            () => State.Failures.Count);

        if (!State.Active || State.Released)
            return Task.CompletedTask;

        var streamProvider = Services.GetRequiredService<IStreamProvider>();
        return _subscriber.EnsureAttachedAsync(streamProvider, State.RootActorId, ForwardObservationAsync, ct);
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        await _subscriber.DetachAsync(ct);
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

        var streamProvider = Services.GetRequiredService<IStreamProvider>();
        var attached = await _subscriber.EnsureAttachedAsync(
            streamProvider, State.RootActorId, ForwardObservationAsync, CancellationToken.None);
        if (attached && !State.ObservationAttached)
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

        await _subscriber.DetachAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleReplayAsync(ReplayProjectionFailuresCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!State.Active || State.Released || State.Failures.Count == 0)
            return;

        await _failureTracker!.ReplayAsync(State, command.MaxItems, DispatchObservationAsync);
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
            .On<ProjectionScopeStartedEvent>(ProjectionScopeStateApplier.ApplyStarted)
            .On<ProjectionObservationAttachmentUpdatedEvent>(ProjectionScopeStateApplier.ApplyAttachmentUpdated)
            .On<ProjectionScopeReleasedEvent>(ProjectionScopeStateApplier.ApplyReleased)
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
        CancellationToken ct)
    {
        var context = ResolveScopeContext();
        var result = await ProcessObservationCoreAsync(context, envelope, ct);
        if (!result.Handled)
            return result;

        await PersistDomainEventAsync(new ProjectionScopeWatermarkAdvancedEvent
        {
            LastObservedVersion = result.LastObservedVersion,
            LastSuccessfulVersion = result.LastObservedVersion,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        });
        return result;
    }

    private TContext ResolveScopeContext()
    {
        var factory = Services.GetRequiredService<Func<ProjectionRuntimeScopeKey, TContext>>();
        return factory(BuildScopeKey());
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

    protected ValueTask RecordDispatchFailureAsync(
        string stage,
        string eventId,
        string eventType,
        long sourceVersion,
        string reason,
        EventEnvelope envelope)
    {
        return _failureTracker!.RecordAsync(stage, eventId, eventType, sourceVersion, reason, envelope, _logger);
    }
}

public readonly record struct ProjectionScopeDispatchResult(
    bool Handled,
    long LastObservedVersion,
    string EventType)
{
    public static ProjectionScopeDispatchResult Skip(string eventType = "") =>
        new(false, 0, eventType);

    public static ProjectionScopeDispatchResult Success(
        long observedVersion,
        string eventType) =>
        new(true, observedVersion, eventType);
}
