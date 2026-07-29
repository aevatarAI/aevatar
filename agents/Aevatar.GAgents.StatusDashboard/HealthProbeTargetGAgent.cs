using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StatusDashboard.Executors;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Per-target probe actor. Only the target descriptor is event sourced;
/// recurring samples are ephemeral operational state overwritten in the
/// configured snapshot store.
/// </summary>
[GAgent("status.dashboard.health-probe-target")]
public sealed class HealthProbeTargetGAgent : GAgentBase<HealthProbeTargetState>
{
    internal const string LegacyProjectionKind = "health-probe-target";

    internal const int RetainedOutcomeCount = 120;
    private static readonly TimeSpan RetainedOutcomeWindow = TimeSpan.FromHours(2);

    private HealthProbeTargetState _runtimeState = new();
    private TimeProvider? _cachedTimeProvider;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _nextTickCts;
    private CancellationTokenSource? _executionTimeoutCts;

    [EventHandler(EndpointName = "configure")]
    public async Task HandleConfigureAsync(HealthProbeConfigureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Spec == null || string.IsNullOrWhiteSpace(command.Spec.Slug))
        {
            Logger.LogWarning("Ignoring HealthProbeConfigureCommand with missing descriptor for actor {ActorId}", Id);
            return;
        }

        if (DescriptorsEquivalent(State.Spec, command.Spec))
            return;

        await PersistDomainEventAsync(new HealthProbeConfigured
        {
            Spec = command.Spec,
            ConfiguredAt = Timestamp.FromDateTimeOffset(ResolveTimeProvider().GetUtcNow()),
        });

        CancelAndDispose(ref _nextTickCts);
        CancelAndDispose(ref _executionTimeoutCts);
        _runtimeState = NewRuntimeState(State.Spec);
        await TryWriteOperationalSnapshotAsync();
        ScheduleNextTick(initial: true);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleTickAsync(HealthProbeTickRequested tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        CancelAndDispose(ref _nextTickCts);

        var descriptor = _runtimeState.Spec;
        if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Slug))
        {
            Logger.LogDebug("Tick fired for unconfigured probe actor {ActorId} — dropping", Id);
            return;
        }

        if (!descriptor.Enabled)
        {
            Logger.LogDebug("Probe {Slug} is disabled — skipping tick", descriptor.Slug);
            return;
        }

        if (_runtimeState.ActiveExecution != null)
        {
            Logger.LogDebug("Probe {Slug} already has an active execution — ignoring duplicate tick", descriptor.Slug);
            return;
        }

        await StartProbeExecutionAsync(descriptor);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleTimeoutFiredAsync(HealthProbeTimeoutFiredEvent timeout)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        var active = _runtimeState.ActiveExecution;
        if (!Matches(active, timeout.OperationId))
            return;

        CancelAndDispose(ref _executionTimeoutCts);
        var timedOutAt = timeout.TimedOutAt ?? Timestamp.FromDateTimeOffset(ResolveTimeProvider().GetUtcNow());
        var startedAt = active!.StartedAt?.ToDateTimeOffset() ?? timedOutAt.ToDateTimeOffset();
        var timeoutMs = timeout.TimeoutMs > 0 ? timeout.TimeoutMs : active.TimeoutMs;
        ApplyOutcome(new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Down,
            LatencyMs = Math.Max(0, (int)Math.Round((timedOutAt.ToDateTimeOffset() - startedAt).TotalMilliseconds)),
            Detail = "timeout",
            ErrorMessage = $"Probe '{_runtimeState.Spec?.ProbeKind ?? active.Slug}' exceeded {timeoutMs}ms.",
            ObservedAt = timedOutAt,
        }, active.OperationId);
        await TryWriteOperationalSnapshotAsync();
        ScheduleNextTick(initial: false);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleCompletedAsync(HealthProbeCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var active = _runtimeState.ActiveExecution;
        if (!Matches(active, completed.OperationId))
            return;

        CancelAndDispose(ref _executionTimeoutCts);
        ApplyOutcome(completed.Outcome, active!.OperationId);
        await TryWriteOperationalSnapshotAsync();
        ScheduleNextTick(initial: false);
    }

    private async Task StartProbeExecutionAsync(HealthProbeTargetDescriptor descriptor)
    {
        var registry = Services.GetService<IHealthProbeExecutorRegistry>();
        var timeProvider = ResolveTimeProvider();
        var observedAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow());

        if (registry == null)
        {
            await ObserveImmediateOutcomeAsync(new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                Detail = "registry_unavailable",
                ErrorMessage = "IHealthProbeExecutorRegistry is not registered in DI.",
                ObservedAt = observedAt,
            });
            return;
        }

        var executor = registry.Resolve(descriptor.ProbeKind);
        if (executor == null)
        {
            await ObserveImmediateOutcomeAsync(new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                Detail = "unknown_probe_kind",
                ErrorMessage = $"No executor registered for probe kind '{descriptor.ProbeKind}'.",
                ObservedAt = observedAt,
            });
            return;
        }

        var timeoutMs = descriptor.TimeoutMs > 0 ? descriptor.TimeoutMs : 5_000;
        var execution = new HealthProbeExecutionState
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Slug = descriptor.Slug,
            StartedAt = observedAt,
            TimeoutMs = timeoutMs,
        };
        _runtimeState.ActiveExecution = execution;
        ScheduleExecutionTimeout(execution);
        _ = ExecuteProbeAndSignalAsync(
            executor,
            descriptor.Clone(),
            execution.Clone(),
            timeProvider.GetTimestamp(),
            _lifetimeCts?.Token ?? CancellationToken.None);
    }

    private async Task ExecuteProbeAndSignalAsync(
        IHealthProbeExecutor executor,
        HealthProbeTargetDescriptor descriptor,
        HealthProbeExecutionState execution,
        long startedAt,
        CancellationToken ct)
    {
        var timeProvider = ResolveTimeProvider();
        HealthProbeOutcome outcome;
        try
        {
            outcome = await executor.ProbeAsync(descriptor, ct);
            outcome.LatencyMs = ToLatencyMs(timeProvider.GetElapsedTime(startedAt));
            outcome.ObservedAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Probe '{Kind}' for {Slug} threw unexpectedly", descriptor.ProbeKind, descriptor.Slug);
            outcome = new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                LatencyMs = ToLatencyMs(timeProvider.GetElapsedTime(startedAt)),
                Detail = "exception",
                ErrorMessage = ex.Message,
                ObservedAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
            };
        }

        try
        {
            await EventPublisher.PublishAsync(
                new HealthProbeCompletedEvent
                {
                    Slug = descriptor.Slug,
                    OperationId = execution.OperationId,
                    Outcome = outcome,
                },
                TopologyAudience.Self,
                ct,
                sourceEnvelope: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to publish health-probe completion for actor {ActorId}", Id);
        }
    }

    private async Task ObserveImmediateOutcomeAsync(HealthProbeOutcome outcome)
    {
        ApplyOutcome(outcome, operationId: null);
        await TryWriteOperationalSnapshotAsync();
        ScheduleNextTick(initial: false);
    }

    private void ApplyOutcome(HealthProbeOutcome outcome, string? operationId)
    {
        var cloned = outcome?.Clone() ?? new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Unknown,
            ObservedAt = Timestamp.FromDateTimeOffset(ResolveTimeProvider().GetUtcNow()),
        };
        _runtimeState.LastOutcome = cloned;
        _runtimeState.LastCheckAt = cloned.ObservedAt;
        _runtimeState.RecentOutcomes.Add(cloned.Clone());
        TrimRecentOutcomes(_runtimeState, cloned.ObservedAt);

        if (cloned.Status == HealthOutcomeStatus.Ok)
        {
            _runtimeState.ConsecutiveFailures = 0;
            _runtimeState.LastSuccessAt = cloned.ObservedAt;
        }
        else
        {
            _runtimeState.ConsecutiveFailures += 1;
        }

        if (Matches(_runtimeState.ActiveExecution, operationId))
            _runtimeState.ActiveExecution = null;
    }

    private void ScheduleNextTick(bool initial)
    {
        CancelAndDispose(ref _nextTickCts);
        var descriptor = _runtimeState.Spec;
        if (descriptor == null || !descriptor.Enabled || _lifetimeCts == null)
            return;

        var dueTime = initial
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(descriptor.IntervalSeconds > 0 ? descriptor.IntervalSeconds : 60);
        _nextTickCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _ = DelayAndPublishSelfAsync(
            dueTime,
            new HealthProbeTickRequested
            {
                Slug = descriptor.Slug,
                ScheduledFor = Timestamp.FromDateTimeOffset(ResolveTimeProvider().GetUtcNow().Add(dueTime)),
            },
            _nextTickCts.Token);
    }

    private void ScheduleExecutionTimeout(HealthProbeExecutionState execution)
    {
        CancelAndDispose(ref _executionTimeoutCts);
        if (_lifetimeCts == null)
            return;

        var dueTime = TimeSpan.FromMilliseconds(execution.TimeoutMs);
        _executionTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _ = DelayAndPublishSelfAsync(
            dueTime,
            new HealthProbeTimeoutFiredEvent
            {
                Slug = execution.Slug,
                OperationId = execution.OperationId,
                TimedOutAt = Timestamp.FromDateTimeOffset(
                    (execution.StartedAt?.ToDateTimeOffset() ?? ResolveTimeProvider().GetUtcNow()).Add(dueTime)),
                TimeoutMs = execution.TimeoutMs,
            },
            _executionTimeoutCts.Token);
    }

    private async Task DelayAndPublishSelfAsync<TEvent>(
        TimeSpan delay,
        TEvent evt,
        CancellationToken ct)
        where TEvent : IMessage
    {
        try
        {
            await Task.Delay(delay, ResolveTimeProvider(), ct);
            ct.ThrowIfCancellationRequested();
            await EventPublisher.PublishAsync(
                evt,
                TopologyAudience.Self,
                CancellationToken.None,
                sourceEnvelope: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to publish delayed health-probe self signal for actor {ActorId}", Id);
        }
    }

    private async Task TryWriteOperationalSnapshotAsync(CancellationToken ct = default)
    {
        var spec = _runtimeState.Spec;
        if (spec == null || string.IsNullOrWhiteSpace(spec.Slug))
            return;

        var snapshot = new HealthProbeOperationalSnapshot
        {
            Target = spec.Clone(),
            ConsecutiveFailures = _runtimeState.ConsecutiveFailures,
            UpdatedAt = Timestamp.FromDateTimeOffset(ResolveTimeProvider().GetUtcNow()),
        };
        if (_runtimeState.LastOutcome != null)
            snapshot.LastOutcome = _runtimeState.LastOutcome.Clone();
        if (_runtimeState.LastCheckAt != null)
            snapshot.LastCheckAt = _runtimeState.LastCheckAt.Clone();
        if (_runtimeState.LastSuccessAt != null)
            snapshot.LastSuccessAt = _runtimeState.LastSuccessAt.Clone();
        snapshot.RecentOutcomes.AddRange(_runtimeState.RecentOutcomes.Select(static outcome => outcome.Clone()));

        try
        {
            var store = Services.GetService<IHealthProbeOperationalSnapshotStore>()
                ?? throw new InvalidOperationException("IHealthProbeOperationalSnapshotStore is not registered.");
            await store.UpsertAsync(snapshot, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to overwrite operational health snapshot for probe {Slug}", spec.Slug);
        }
    }

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);
        CancelAndDispose(ref _lifetimeCts);
        _lifetimeCts = new CancellationTokenSource();
        State = NewRuntimeState(State.Spec);
        _runtimeState = State.Clone();
        await TryWriteOperationalSnapshotAsync(ct);
        await TryPurgeLegacyDurableCallbacksAsync(ct);
        ScheduleNextTick(initial: true);
    }

    protected override async Task OnDeactivateAsync(CancellationToken ct)
    {
        CancelAndDispose(ref _nextTickCts);
        CancelAndDispose(ref _executionTimeoutCts);
        CancelAndDispose(ref _lifetimeCts);
        await base.OnDeactivateAsync(ct);
    }

    private async Task TryPurgeLegacyDurableCallbacksAsync(CancellationToken ct)
    {
        try
        {
            await PurgeDurableCallbacksAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to purge legacy durable health-probe callbacks for actor {ActorId}", Id);
        }
    }

    protected override HealthProbeTargetState TransitionState(HealthProbeTargetState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<HealthProbeConfigured>(ApplyConfigured)
            .On<HealthProbeObserved>(ApplyObserved)
            .On<HealthProbeExecutionStarted>(ApplyExecutionStarted)
            .On<HealthProbeExecutionCleared>(ApplyExecutionCleared)
            .OrCurrent();

    private static HealthProbeTargetState ApplyConfigured(HealthProbeTargetState state, HealthProbeConfigured evt)
    {
        var next = state.Clone();
        next.Spec = evt.Spec?.Clone();
        next.LastOutcome = null;
        next.LastCheckAt = null;
        next.LastSuccessAt = null;
        next.ConsecutiveFailures = 0;
        next.RecentOutcomes.Clear();
        next.ActiveExecution = null;
        return next;
    }

    private static HealthProbeTargetState ApplyObserved(HealthProbeTargetState state, HealthProbeObserved evt)
    {
        var next = state.Clone();
        var outcome = evt.Outcome?.Clone();
        next.LastOutcome = outcome;
        next.LastCheckAt = outcome?.ObservedAt;
        if (outcome != null)
        {
            next.RecentOutcomes.Add(outcome.Clone());
            TrimRecentOutcomes(next, outcome.ObservedAt);
        }

        if (outcome?.Status == HealthOutcomeStatus.Ok)
        {
            next.ConsecutiveFailures = 0;
            next.LastSuccessAt = outcome.ObservedAt;
        }
        else
        {
            next.ConsecutiveFailures += 1;
        }

        if (Matches(next.ActiveExecution, evt.OperationId))
            next.ActiveExecution = null;
        return next;
    }

    private static HealthProbeTargetState ApplyExecutionStarted(
        HealthProbeTargetState state,
        HealthProbeExecutionStarted evt)
    {
        var next = state.Clone();
        next.ActiveExecution = evt.Execution?.Clone();
        return next;
    }

    private static HealthProbeTargetState ApplyExecutionCleared(
        HealthProbeTargetState state,
        HealthProbeExecutionCleared evt)
    {
        var next = state.Clone();
        if (Matches(next.ActiveExecution, evt.OperationId))
            next.ActiveExecution = null;
        return next;
    }

    private static HealthProbeTargetState NewRuntimeState(HealthProbeTargetDescriptor? spec) =>
        new() { Spec = spec?.Clone() };

    private static bool Matches(HealthProbeExecutionState? execution, string? operationId) =>
        execution != null &&
        !string.IsNullOrWhiteSpace(operationId) &&
        string.Equals(execution.OperationId, operationId, StringComparison.Ordinal);

    private static void TrimRecentOutcomes(HealthProbeTargetState state, Timestamp? latestObservedAt)
    {
        if (latestObservedAt != null)
        {
            var cutoff = latestObservedAt.ToDateTimeOffset() - RetainedOutcomeWindow;
            while (state.RecentOutcomes.Count > 0 && IsBefore(state.RecentOutcomes[0].ObservedAt, cutoff))
                state.RecentOutcomes.RemoveAt(0);
        }

        while (state.RecentOutcomes.Count > RetainedOutcomeCount)
            state.RecentOutcomes.RemoveAt(0);
    }

    private static bool IsBefore(Timestamp? timestamp, DateTimeOffset cutoff) =>
        timestamp != null && timestamp.ToDateTimeOffset() < cutoff;

    private TimeProvider ResolveTimeProvider() =>
        _cachedTimeProvider ??= Services.GetService<TimeProvider>() ?? TimeProvider.System;

    private static int ToLatencyMs(TimeSpan elapsed) =>
        (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue);

    private static bool DescriptorsEquivalent(HealthProbeTargetDescriptor? a, HealthProbeTargetDescriptor? b) =>
        ReferenceEquals(a, b) || (a != null && b != null && a.Equals(b));

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = null;
    }
}
