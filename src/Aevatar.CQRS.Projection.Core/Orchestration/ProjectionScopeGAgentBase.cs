using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Stores.Abstractions;
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

        await _failureTracker!.ReplayAsync(State, command.MaxItems, DispatchObservationAsync);
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
            .On<ProjectionScopeWatermarkAdvancedEvent>(ProjectionScopeStateApplier.ApplyWatermarkAdvanced)
            .On<ProjectionScopeDispatchFailedEvent>(ProjectionScopeStateApplier.ApplyDispatchFailed)
            .On<ProjectionScopeFailureReplayedEvent>(ProjectionScopeStateApplier.ApplyFailureReplayed)
            .OrCurrent();

    // Refactor (iter18/cluster-003):
    //   Old pattern: Projection watermark query port reads IEventStore and rebuilds ProjectionScopeState in the query call.
    //   New principle: Watermark queries read a materialized actor-owned/projection-owned read model, with rebuild only in maintenance paths.
    // New pattern: query reads materialized watermark; event replay is repair/recovery only.
    protected override async Task OnStateChangedAsync(ProjectionScopeState state, CancellationToken ct)
    {
        var writer = Services.GetService<IProjectionDocumentWriter<ProjectionScopeWatermarkReadModel>>();
        if (writer == null || string.IsNullOrWhiteSpace(state.RootActorId) || string.IsNullOrWhiteSpace(state.ProjectionKind))
            return;

        var scopeKey = new ProjectionRuntimeScopeKey(
            state.RootActorId,
            state.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(state.Mode),
            state.SessionId);
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);
        var currentVersion = EventSourcing?.CurrentVersion ?? state.LastObservedVersion;
        await writer.UpsertAsync(
            new ProjectionScopeWatermarkReadModel
            {
                Id = scopeActorId,
                RootActorId = state.RootActorId,
                ProjectionKind = state.ProjectionKind,
                SessionId = state.SessionId,
                Mode = state.Mode,
                Active = state.Active,
                Released = state.Released,
                LastObservedVersion = state.LastObservedVersion,
                LastSuccessfulVersion = state.LastSuccessfulVersion,
                StateVersion = currentVersion,
                UpdatedAtUtc = state.UpdatedAtUtc?.Clone() ?? Timestamp.FromDateTime(DateTime.UtcNow),
            },
            ct).ConfigureAwait(false);
    }

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
        var typeUrl = $"type.googleapis.com/{CommittedStateEventPublished.Descriptor.FullName}";
        return new StreamForwardingBinding
        {
            SourceStreamId = rootActorId,
            TargetStreamId = Id,
            ForwardingMode = StreamForwardingMode.HandleThenForward,
            DirectionFilter = [],
            EventTypeFilter = new HashSet<string>(StringComparer.Ordinal)
            {
                typeUrl,
            },
        };
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
