using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;

public sealed class OrleansActorRuntimeCallbackScheduler : IActorRuntimeCallbackScheduler
{
    private enum SchedulingMode
    {
        Auto = 0,
        ForceInline = 1,
        ForceDedicated = 2,
    }

    private enum DedicatedDeliveryMode
    {
        Auto = 0,
        Timer = 1,
        Reminder = 2,
    }

    private readonly IGrainFactory _grainFactory;
    private readonly IRuntimeActorInlineCallbackSchedulerBindingAccessor? _inlineSchedulerBindingAccessor;
    private readonly SchedulingMode _schedulingMode;
    private readonly DedicatedDeliveryMode _dedicatedDeliveryMode;
    private readonly int _inlineMaxDueTimeMs;
    private readonly int _reminderThresholdMs;

    public OrleansActorRuntimeCallbackScheduler(
        IGrainFactory grainFactory,
        IRuntimeActorInlineCallbackSchedulerBindingAccessor? inlineSchedulerBindingAccessor = null,
        AevatarOrleansRuntimeOptions? options = null)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
        _inlineSchedulerBindingAccessor = inlineSchedulerBindingAccessor;
        var runtimeOptions = options ?? new AevatarOrleansRuntimeOptions();
        _schedulingMode = ParseSchedulingMode(runtimeOptions.RuntimeCallbackSchedulingMode);
        _dedicatedDeliveryMode = ParseDedicatedDeliveryMode(runtimeOptions.RuntimeCallbackDedicatedDeliveryMode);
        _inlineMaxDueTimeMs = runtimeOptions.RuntimeCallbackInlineMaxDueTimeMs;
        _reminderThresholdMs = runtimeOptions.RuntimeCallbackReminderThresholdMs;
    }

    public async Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
        RuntimeCallbackTimeoutRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ct.ThrowIfCancellationRequested();

        var envelope = RuntimeCallbackEnvelopeFactory.CreateSelfEnvelope(request.ActorId, request.TriggerEnvelope);
        if (TryResolveInlineScheduler(request.ActorId, request.DueTime, out var inlineScheduler))
        {
            return await inlineScheduler.ScheduleTimeoutAsync(
                request.CallbackId,
                envelope,
                request.DueTime,
                ct);
        }

        var generation = await ScheduleViaDedicatedGrainTimeoutAsync(
            request.ActorId,
            request.CallbackId,
            envelope,
            request.DueTime,
            period: null);

        return new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            generation,
            RuntimeCallbackBackend.Dedicated);
    }

    public async Task<RuntimeCallbackLease> ScheduleTimerAsync(
        RuntimeCallbackTimerRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Period, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var envelope = RuntimeCallbackEnvelopeFactory.CreateSelfEnvelope(request.ActorId, request.TriggerEnvelope);
        if (TryResolveInlineScheduler(request.ActorId, request.DueTime, out var inlineScheduler))
        {
            return await inlineScheduler.ScheduleTimerAsync(
                request.CallbackId,
                envelope,
                request.DueTime,
                request.Period,
                ct);
        }

        var generation = await ScheduleViaDedicatedGrainTimerAsync(
            request.ActorId,
            request.CallbackId,
            envelope,
            request.DueTime,
            request.Period);

        return new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            generation,
            RuntimeCallbackBackend.Dedicated);
    }

    public Task CancelAsync(
        RuntimeCallbackLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();

        return lease.Backend switch
        {
            RuntimeCallbackBackend.Inline => CancelInlineAsync(lease, ct),
            RuntimeCallbackBackend.Dedicated => CancelDedicatedCallbackAsync(lease.ActorId, lease.CallbackId, lease.Generation),
            _ => throw new InvalidOperationException($"Orleans callback scheduler cannot cancel backend '{lease.Backend}'."),
        };
    }

    private bool TryResolveInlineScheduler(
        string actorId,
        TimeSpan dueTime,
        out IRuntimeActorInlineCallbackScheduler inlineScheduler)
    {
        if (_schedulingMode == SchedulingMode.ForceDedicated)
        {
            inlineScheduler = null!;
            return false;
        }

        if (!TryGetInlineScheduler(actorId, out inlineScheduler))
        {
            if (_schedulingMode == SchedulingMode.ForceInline)
            {
                throw new InvalidOperationException(
                    $"Orleans runtime callback scheduler is configured as ForceInline, but no inline grain turn binding is available for actor '{actorId}'.");
            }

            return false;
        }

        if (_schedulingMode == SchedulingMode.ForceInline)
            return true;

        if (_inlineMaxDueTimeMs > 0 && dueTime.TotalMilliseconds > _inlineMaxDueTimeMs)
            return false;

        return true;
    }

    private Task CancelInlineAsync(RuntimeCallbackLease lease, CancellationToken ct)
    {
        if (!TryGetInlineScheduler(lease.ActorId, out var inlineScheduler))
        {
            throw new InvalidOperationException(
                $"Orleans callback scheduler cannot cancel inline callback '{lease.CallbackId}' for actor '{lease.ActorId}' outside the owning grain turn.");
        }

        return inlineScheduler.CancelAsync(lease.CallbackId, lease.Generation, ct);
    }

    private bool TryGetInlineScheduler(
        string actorId,
        out IRuntimeActorInlineCallbackScheduler inlineScheduler)
    {
        inlineScheduler = _inlineSchedulerBindingAccessor?.Current!;
        return inlineScheduler != null &&
               string.Equals(inlineScheduler.ActorId, actorId, StringComparison.Ordinal);
    }

    private async Task<long> ScheduleViaDedicatedGrainTimeoutAsync(
        string actorId,
        string callbackId,
        EventEnvelope envelope,
        TimeSpan dueTime,
        TimeSpan? period)
    {
        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return await grain.ScheduleTimeoutAsync(
            callbackId,
            envelope.ToByteArray(),
            ToPositiveMilliseconds(dueTime),
            ResolveDedicatedDeliveryMode(dueTime, period));
    }

    private async Task<long> ScheduleViaDedicatedGrainTimerAsync(
        string actorId,
        string callbackId,
        EventEnvelope envelope,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return await grain.ScheduleTimerAsync(
            callbackId,
            envelope.ToByteArray(),
            ToPositiveMilliseconds(dueTime),
            ToPositiveMilliseconds(period),
            ResolveDedicatedDeliveryMode(dueTime, period));
    }

    private Task CancelDedicatedCallbackAsync(
        string actorId,
        string callbackId,
        long expectedGeneration = 0)
    {
        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return grain.CancelAsync(callbackId, expectedGeneration);
    }

    private RuntimeCallbackDeliveryMode ResolveDedicatedDeliveryMode(
        TimeSpan dueTime,
        TimeSpan? period)
    {
        return _dedicatedDeliveryMode switch
        {
            DedicatedDeliveryMode.Timer => RuntimeCallbackDeliveryMode.Timer,
            DedicatedDeliveryMode.Reminder => RuntimeCallbackDeliveryMode.Reminder,
            _ => ShouldUseReminderInAuto(dueTime, period)
                ? RuntimeCallbackDeliveryMode.Reminder
                : RuntimeCallbackDeliveryMode.Timer,
        };
    }

    private bool ShouldUseReminderInAuto(TimeSpan dueTime, TimeSpan? period)
    {
        if (_reminderThresholdMs <= 0)
            return false;

        if (dueTime.TotalMilliseconds >= _reminderThresholdMs)
            return true;

        if (period.HasValue && period.Value.TotalMilliseconds >= _reminderThresholdMs)
            return true;

        return false;
    }

    private static SchedulingMode ParseSchedulingMode(string mode)
    {
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.RuntimeCallbackSchedulingModeForceInline, StringComparison.OrdinalIgnoreCase))
            return SchedulingMode.ForceInline;
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.RuntimeCallbackSchedulingModeForceDedicated, StringComparison.OrdinalIgnoreCase))
            return SchedulingMode.ForceDedicated;
        return SchedulingMode.Auto;
    }

    private static DedicatedDeliveryMode ParseDedicatedDeliveryMode(string mode)
    {
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.RuntimeCallbackDedicatedDeliveryModeTimer, StringComparison.OrdinalIgnoreCase))
            return DedicatedDeliveryMode.Timer;
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.RuntimeCallbackDedicatedDeliveryModeReminder, StringComparison.OrdinalIgnoreCase))
            return DedicatedDeliveryMode.Reminder;
        return DedicatedDeliveryMode.Auto;
    }

    private static void ValidateRequest(
        string actorId,
        string callbackId,
        EventEnvelope envelope,
        TimeSpan dueTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Payload);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTime, TimeSpan.Zero);
    }

    private static int ToPositiveMilliseconds(TimeSpan duration)
    {
        var ms = (int)Math.Ceiling(duration.TotalMilliseconds);
        return Math.Clamp(ms, 1, int.MaxValue);
    }
}
