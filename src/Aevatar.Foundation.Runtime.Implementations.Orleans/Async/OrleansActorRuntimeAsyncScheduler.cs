using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Async;
using Aevatar.Foundation.Runtime.Async;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Async;

public sealed class OrleansActorRuntimeAsyncScheduler : IActorRuntimeAsyncScheduler
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

    public OrleansActorRuntimeAsyncScheduler(
        IGrainFactory grainFactory,
        IRuntimeActorInlineCallbackSchedulerBindingAccessor? inlineSchedulerBindingAccessor = null,
        AevatarOrleansRuntimeOptions? options = null)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
        _inlineSchedulerBindingAccessor = inlineSchedulerBindingAccessor;
        var runtimeOptions = options ?? new AevatarOrleansRuntimeOptions();
        _schedulingMode = ParseSchedulingMode(runtimeOptions.AsyncCallbackSchedulingMode);
        _dedicatedDeliveryMode = ParseDedicatedDeliveryMode(runtimeOptions.AsyncCallbackDedicatedDeliveryMode);
        _inlineMaxDueTimeMs = runtimeOptions.AsyncCallbackInlineMaxDueTimeMs;
        _reminderThresholdMs = runtimeOptions.AsyncCallbackReminderThresholdMs;
    }

    public async Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
        RuntimeTimeoutRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ct.ThrowIfCancellationRequested();

        var envelope = RuntimeScheduledEnvelopeFactory.CreateSelfEnvelope(request.ActorId, request.TriggerEnvelope);
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

        return new RuntimeCallbackLease(request.ActorId, request.CallbackId, generation);
    }

    public async Task<RuntimeCallbackLease> ScheduleTimerAsync(
        RuntimeTimerRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Period, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var envelope = RuntimeScheduledEnvelopeFactory.CreateSelfEnvelope(request.ActorId, request.TriggerEnvelope);
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

        return new RuntimeCallbackLease(request.ActorId, request.CallbackId, generation);
    }

    public Task CancelAsync(
        string actorId,
        string callbackId,
        long? expectedGeneration = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ct.ThrowIfCancellationRequested();

        if (TryResolveInlineSchedulerForCancel(actorId, out var inlineScheduler))
        {
            if (_schedulingMode == SchedulingMode.ForceDedicated)
            {
                var dedicatedGrain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
                return dedicatedGrain.CancelAsync(callbackId, expectedGeneration ?? 0);
            }

            return inlineScheduler.CancelAsync(callbackId, expectedGeneration, ct);
        }

        if (_schedulingMode == SchedulingMode.ForceInline)
        {
            throw new InvalidOperationException(
                $"Orleans async scheduler is configured as ForceInline, but no inline grain turn binding is available for actor '{actorId}'.");
        }

        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return grain.CancelAsync(callbackId, expectedGeneration ?? 0);
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
                    $"Orleans async scheduler is configured as ForceInline, but no inline grain turn binding is available for actor '{actorId}'.");
            }

            return false;
        }

        if (_schedulingMode == SchedulingMode.ForceInline)
            return true;

        if (_inlineMaxDueTimeMs > 0 && dueTime.TotalMilliseconds > _inlineMaxDueTimeMs)
            return false;

        return true;
    }

    private bool TryResolveInlineSchedulerForCancel(
        string actorId,
        out IRuntimeActorInlineCallbackScheduler inlineScheduler)
    {
        if (_schedulingMode == SchedulingMode.ForceDedicated)
        {
            inlineScheduler = null!;
            return false;
        }

        return TryGetInlineScheduler(actorId, out inlineScheduler);
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
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.AsyncCallbackSchedulingModeForceInline, StringComparison.OrdinalIgnoreCase))
            return SchedulingMode.ForceInline;
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.AsyncCallbackSchedulingModeForceDedicated, StringComparison.OrdinalIgnoreCase))
            return SchedulingMode.ForceDedicated;
        return SchedulingMode.Auto;
    }

    private static DedicatedDeliveryMode ParseDedicatedDeliveryMode(string mode)
    {
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.AsyncCallbackDedicatedDeliveryModeTimer, StringComparison.OrdinalIgnoreCase))
            return DedicatedDeliveryMode.Timer;
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.AsyncCallbackDedicatedDeliveryModeReminder, StringComparison.OrdinalIgnoreCase))
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
