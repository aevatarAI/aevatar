using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.StatusDashboard.Executors;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Per-target probe actor — one instance per configured health probe target.
/// Owns: the immutable descriptor, the latest probe outcome, and the
/// self-rescheduling tick that drives recurring execution.
///
/// Actor id convention: opaque, derived from the slug at startup (see
/// <see cref="HealthProbeStoreCommands.BuildActorId(string)"/>). Callers query
/// the read model by slug, not by actor id.
/// </summary>
[GAgent("status.dashboard.health-probe-target")]
public sealed class HealthProbeTargetGAgent : GAgentBase<HealthProbeTargetState>, IProjectedActor
{
    public static string ProjectionKind => "health-probe-target";

    internal const string TickCallbackId = "health-probe-tick";
    internal const string ExecutionTimeoutCallbackPrefix = "health-probe-execution-timeout";
    internal const int RetainedOutcomeCount = 120;
    private static readonly TimeSpan RetainedOutcomeWindow = TimeSpan.FromHours(2);
    private TimeProvider? _cachedTimeProvider;

    // ─── Commands ───

    [EventHandler(EndpointName = "configure")]
    public async Task HandleConfigureAsync(HealthProbeConfigureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var timeProvider = ResolveTimeProvider();
        if (command.Spec == null || string.IsNullOrWhiteSpace(command.Spec.Slug))
        {
            Logger.LogWarning("Ignoring HealthProbeConfigureCommand with missing descriptor for actor {ActorId}", Id);
            return;
        }

        if (DescriptorsEquivalent(State.Spec, command.Spec))
        {
            // OnActivateAsync re-arms existing probes after a host restart.
            // Re-arming here as well creates duplicate first ticks during
            // rolling deploys, which can make the persisted probe stream race
            // with itself.
            return;
        }

        await PersistDomainEventAsync(new HealthProbeConfigured
        {
            Spec = command.Spec,
            ConfiguredAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
        });

        await EnsureNextTickAsync(initial: true);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleTickAsync(HealthProbeTickRequested tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        var descriptor = State.Spec;
        if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Slug))
        {
            Logger.LogDebug("Tick fired for unconfigured probe actor {ActorId} — dropping", Id);
            return;
        }

        if (!descriptor.Enabled)
        {
            // Stop the self-rescheduling chain when an operator disables the
            // target via Configure. EnsureNextTick is not called.
            Logger.LogDebug("Probe {Slug} is disabled — skipping tick", descriptor.Slug);
            return;
        }

        await StartProbeExecutionAsync(descriptor);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleTimeoutFiredAsync(HealthProbeTimeoutFiredEvent timeout)
    {
        ArgumentNullException.ThrowIfNull(timeout);
        var active = State.ActiveExecution;
        if (active == null ||
            string.IsNullOrWhiteSpace(timeout.OperationId) ||
            !string.Equals(active.OperationId, timeout.OperationId, StringComparison.Ordinal))
        {
            return;
        }

        var timedOutAt = timeout.TimedOutAt ?? Timestamp.FromDateTimeOffset(ResolveTimeProvider().GetUtcNow());
        var startedAt = active.StartedAt?.ToDateTimeOffset() ?? timedOutAt.ToDateTimeOffset();
        var latencyMs = Math.Max(0, (int)Math.Round((timedOutAt.ToDateTimeOffset() - startedAt).TotalMilliseconds));
        var timeoutMs = timeout.TimeoutMs > 0 ? timeout.TimeoutMs : active.TimeoutMs;
        var descriptor = State.Spec;
        var outcome = new HealthProbeOutcome
        {
            Status = HealthOutcomeStatus.Down,
            LatencyMs = latencyMs,
            Detail = "timeout",
            ErrorMessage = $"Probe '{descriptor?.ProbeKind ?? active.Slug}' exceeded {timeoutMs}ms.",
            ObservedAt = timedOutAt,
        };

        try
        {
            await PersistProbeTerminalEventsAsync(outcome, active.OperationId);
        }
        catch (EventStoreOptimisticConcurrencyException ex)
        {
            Logger.LogInformation(ex,
                "Probe {Slug} timeout outcome lost a concurrent tick race; next tick will be re-armed",
                timeout.Slug);
        }

        await EnsureNextTickAsync(initial: false);
    }

    [EventHandler(AllowSelfHandling = true, OnlySelfHandling = true)]
    public async Task HandleCompletedAsync(HealthProbeCompletedEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var active = State.ActiveExecution;
        if (active == null ||
            string.IsNullOrWhiteSpace(completed.OperationId) ||
            !string.Equals(active.OperationId, completed.OperationId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await PersistProbeTerminalEventsAsync(completed.Outcome, active.OperationId);
        }
        catch (EventStoreOptimisticConcurrencyException ex)
        {
            Logger.LogInformation(ex,
                "Probe {Slug} observed outcome lost a concurrent tick race; next tick will be re-armed",
                completed.Slug);
        }

        await EnsureNextTickAsync(initial: false);
    }

    private Task PersistProbeTerminalEventsAsync(HealthProbeOutcome outcome, string operationId)
    {
        // Refactor (iter158/cluster-157-004-timeout-cts):
        // Old: HealthProbeObserved also cleared ActiveExecution, leaving the typed
        //      HealthProbeExecutionCleared domain event with no producer.
        // New: probe completion persists observed outcome and explicit clear event
        //      together, so execution lifecycle remains actor-owned and observable.
        return PersistDomainEventsAsync([
            new HealthProbeObserved { Outcome = outcome },
            new HealthProbeExecutionCleared { OperationId = operationId },
        ]);
    }

    // ─── Probe execution ───

    private async Task StartProbeExecutionAsync(HealthProbeTargetDescriptor descriptor)
    {
        var registry = Services.GetService<IHealthProbeExecutorRegistry>();
        var timeProvider = ResolveTimeProvider();
        // Refactor (iter89/cluster-089-status-dashboard-probe-clock):
        // Old: sample DateTimeOffset.UtcNow and Stopwatch directly inside the actor.
        // New: use the injected TimeProvider for observed_at, timeout budget, and monotonic elapsed latency.
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

        var startedAt = timeProvider.GetTimestamp();
        var timeoutMs = descriptor.TimeoutMs > 0 ? descriptor.TimeoutMs : 5_000;
        var execution = await RegisterExecutionTimeoutAsync(descriptor, timeoutMs);
        _ = ExecuteProbeAndSignalAsync(executor, descriptor.Clone(), execution, startedAt);
    }

    private async Task ExecuteProbeAndSignalAsync(
        IHealthProbeExecutor executor,
        HealthProbeTargetDescriptor descriptor,
        HealthProbeExecutionState execution,
        long startedAt)
    {
        var timeProvider = ResolveTimeProvider();
        HealthProbeOutcome outcome;
        try
        {
            outcome = await executor.ProbeAsync(descriptor, CancellationToken.None);
            outcome.LatencyMs = ToLatencyMs(timeProvider.GetElapsedTime(startedAt));
            outcome.ObservedAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Probe '{Kind}' for {Slug} threw unexpectedly",
                descriptor.ProbeKind, descriptor.Slug);
            outcome = new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                LatencyMs = ToLatencyMs(timeProvider.GetElapsedTime(startedAt)),
                Detail = "exception",
                ErrorMessage = ex.Message,
                ObservedAt = Timestamp.FromDateTimeOffset(timeProvider.GetUtcNow()),
            };
        }

        await SendToAsync(Id, new HealthProbeCompletedEvent
        {
            Slug = descriptor.Slug,
            OperationId = execution.OperationId,
            Outcome = outcome,
        }, CancellationToken.None);
    }

    private async Task ObserveImmediateOutcomeAsync(HealthProbeOutcome outcome)
    {
        try
        {
            await PersistDomainEventAsync(new HealthProbeObserved { Outcome = outcome });
        }
        catch (EventStoreOptimisticConcurrencyException ex)
        {
            Logger.LogInformation(ex, "Immediate probe outcome lost a concurrent tick race; next tick will be re-armed");
        }

        await EnsureNextTickAsync(initial: false);
    }

    // ─── Self-scheduling ───

    private async Task EnsureNextTickAsync(bool initial)
    {
        var descriptor = State.Spec;
        _ = descriptor; // ensures usage below;
        if (descriptor == null || !descriptor.Enabled) return;

        var intervalSeconds = descriptor.IntervalSeconds > 0 ? descriptor.IntervalSeconds : 60;
        var dueTime = initial
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(intervalSeconds);
        var scheduledFor = ResolveTimeProvider().GetUtcNow().Add(dueTime);

        await ScheduleSelfDurableTimeoutAsync(
            callbackId: TickCallbackId,
            dueTime: dueTime,
            evt: new HealthProbeTickRequested
            {
                Slug = descriptor.Slug,
                ScheduledFor = Timestamp.FromDateTimeOffset(scheduledFor),
            });
    }

    private async Task<HealthProbeExecutionState> RegisterExecutionTimeoutAsync(
        HealthProbeTargetDescriptor descriptor,
        int timeoutMs)
    {
        var now = ResolveTimeProvider().GetUtcNow();
        var ticks = ResolveTimeProvider().GetTimestamp();
        var operationId = RuntimeCallbackKeyComposer.BuildCallbackId(
            "health-probe-operation",
            descriptor.Slug,
            now.ToUnixTimeMilliseconds().ToString(),
            ticks.ToString());
        var execution = new HealthProbeExecutionState
        {
            OperationId = operationId,
            Slug = descriptor.Slug,
            StartedAt = Timestamp.FromDateTimeOffset(now),
            TimeoutMs = timeoutMs,
        };
        await PersistDomainEventAsync(new HealthProbeExecutionStarted
        {
            Execution = execution.Clone(),
        });

        await ScheduleSelfDurableTimeoutAsync(
            RuntimeCallbackKeyComposer.BuildCallbackId(
                ExecutionTimeoutCallbackPrefix,
                descriptor.Slug,
                operationId),
            TimeSpan.FromMilliseconds(timeoutMs),
            new HealthProbeTimeoutFiredEvent
            {
                Slug = descriptor.Slug,
                OperationId = operationId,
                TimedOutAt = Timestamp.FromDateTimeOffset(now.AddMilliseconds(timeoutMs)),
                TimeoutMs = timeoutMs,
            });

        return execution;
    }

    // ─── Activation ───

    protected override async Task OnActivateAsync(CancellationToken ct)
    {
        await base.OnActivateAsync(ct);

        if (State.Spec != null && State.Spec.Enabled)
        {
            // Local runtime loses callback state across restarts — re-arm the
            // self-tick chain so probing resumes without operator action. The
            // Orleans reminder backend is idempotent under the same callback id.
            await EnsureNextTickAsync(initial: true);
        }
    }

    // ─── State reducer ───

    protected override HealthProbeTargetState TransitionState(HealthProbeTargetState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<HealthProbeConfigured>(ApplyConfigured)
            .On<HealthProbeObserved>(ApplyObserved)
            .On<HealthProbeExecutionStarted>(ApplyExecutionStarted)
            .On<HealthProbeExecutionCleared>(ApplyExecutionCleared)
            .OrCurrent();

    private static HealthProbeTargetState ApplyConfigured(HealthProbeTargetState s, HealthProbeConfigured evt)
    {
        var next = s.Clone();
        next.Spec = evt.Spec?.Clone();
        next.LastOutcome = null;
        next.LastCheckAt = null;
        next.LastSuccessAt = null;
        next.ConsecutiveFailures = 0;
        next.RecentOutcomes.Clear();
        next.ActiveExecution = null;
        return next;
    }

    private static HealthProbeTargetState ApplyObserved(HealthProbeTargetState s, HealthProbeObserved evt)
    {
        var next = s.Clone();
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
        return next;
    }

    private static HealthProbeTargetState ApplyExecutionStarted(HealthProbeTargetState s, HealthProbeExecutionStarted evt)
    {
        var next = s.Clone();
        next.ActiveExecution = evt.Execution?.Clone();
        return next;
    }

    private static HealthProbeTargetState ApplyExecutionCleared(HealthProbeTargetState s, HealthProbeExecutionCleared evt)
    {
        var next = s.Clone();
        if (next.ActiveExecution != null &&
            string.Equals(next.ActiveExecution.OperationId, evt.OperationId, StringComparison.Ordinal))
        {
            next.ActiveExecution = null;
        }
        return next;
    }

    private static void TrimRecentOutcomes(HealthProbeTargetState state, Timestamp? latestObservedAt)
    {
        if (latestObservedAt != null)
        {
            var cutoff = latestObservedAt.ToDateTimeOffset() - RetainedOutcomeWindow;
            while (state.RecentOutcomes.Count > 0 &&
                   IsBefore(state.RecentOutcomes[0].ObservedAt, cutoff))
            {
                state.RecentOutcomes.RemoveAt(0);
            }
        }

        while (state.RecentOutcomes.Count > RetainedOutcomeCount)
        {
            state.RecentOutcomes.RemoveAt(0);
        }
    }

    private static bool IsBefore(Timestamp? timestamp, DateTimeOffset cutoff) =>
        timestamp != null && timestamp.ToDateTimeOffset() < cutoff;

    private TimeProvider ResolveTimeProvider() =>
        _cachedTimeProvider ??= Services.GetService<TimeProvider>() ?? TimeProvider.System;

    private static int ToLatencyMs(TimeSpan elapsed) =>
        (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue);

    private static bool DescriptorsEquivalent(HealthProbeTargetDescriptor? a, HealthProbeTargetDescriptor? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.Equals(b);
    }
}
