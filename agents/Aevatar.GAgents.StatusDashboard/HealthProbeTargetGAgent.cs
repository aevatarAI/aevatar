using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
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
public sealed class HealthProbeTargetGAgent : GAgentBase<HealthProbeTargetState>, IProjectedActor
{
    public static string ProjectionKind => HealthProbeProjectionPort.ProjectionKind;

    internal const string TickCallbackId = "health-probe-tick";
    internal const int RetainedOutcomeCount = 120;
    private static readonly TimeSpan RetainedOutcomeWindow = TimeSpan.FromHours(2);

    // ─── Commands ───

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
        {
            // No descriptor change — still ensure the timer is alive in case
            // a Local-runtime restart cleared scheduler state.
            await EnsureNextTickAsync(initial: true);
            return;
        }

        await PersistDomainEventAsync(new HealthProbeConfigured
        {
            Spec = command.Spec,
            ConfiguredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
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

        var outcome = await ExecuteProbeWithGuardsAsync(descriptor);

        await PersistDomainEventAsync(new HealthProbeObserved { Outcome = outcome });

        await EnsureNextTickAsync(initial: false);
    }

    // ─── Probe execution ───

    private async Task<HealthProbeOutcome> ExecuteProbeWithGuardsAsync(HealthProbeTargetDescriptor descriptor)
    {
        var registry = Services.GetService<IHealthProbeExecutorRegistry>();
        var observedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);

        if (registry == null)
        {
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                Detail = "registry_unavailable",
                ErrorMessage = "IHealthProbeExecutorRegistry is not registered in DI.",
                ObservedAt = observedAt,
            };
        }

        var executor = registry.Resolve(descriptor.ProbeKind);
        if (executor == null)
        {
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                Detail = "unknown_probe_kind",
                ErrorMessage = $"No executor registered for probe kind '{descriptor.ProbeKind}'.",
                ObservedAt = observedAt,
            };
        }

        var timeoutMs = descriptor.TimeoutMs > 0 ? descriptor.TimeoutMs : 5_000;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var outcome = await executor.ProbeAsync(descriptor, cts.Token);
            sw.Stop();
            // Always overwrite latency and observed_at with the in-actor values
            // so executors stay focused on the upstream signal.
            outcome.LatencyMs = (int)Math.Clamp(sw.ElapsedMilliseconds, 0, int.MaxValue);
            outcome.ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
            return outcome;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            sw.Stop();
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                LatencyMs = (int)Math.Clamp(sw.ElapsedMilliseconds, 0, int.MaxValue),
                Detail = "timeout",
                ErrorMessage = $"Probe '{descriptor.ProbeKind}' exceeded {timeoutMs}ms.",
                ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logger.LogWarning(ex, "Probe '{Kind}' for {Slug} threw unexpectedly",
                descriptor.ProbeKind, descriptor.Slug);
            return new HealthProbeOutcome
            {
                Status = HealthOutcomeStatus.Down,
                LatencyMs = (int)Math.Clamp(sw.ElapsedMilliseconds, 0, int.MaxValue),
                Detail = "exception",
                ErrorMessage = ex.Message,
                ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };
        }
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

        await ScheduleSelfDurableTimeoutAsync(
            callbackId: TickCallbackId,
            dueTime: dueTime,
            evt: new HealthProbeTickRequested
            {
                Slug = descriptor.Slug,
                ScheduledFor = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.Add(dueTime)),
            });
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

    private static bool DescriptorsEquivalent(HealthProbeTargetDescriptor? a, HealthProbeTargetDescriptor? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.Equals(b);
    }
}
