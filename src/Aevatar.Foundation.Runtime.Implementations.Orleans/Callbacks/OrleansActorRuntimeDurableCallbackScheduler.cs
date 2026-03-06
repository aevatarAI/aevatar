using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;

public sealed class OrleansActorRuntimeDurableCallbackScheduler
    : IActorRuntimeCallbackScheduler
{
    private enum DurableDeliveryMode
    {
        Auto = 0,
        Timer = 1,
        Reminder = 2,
    }

    private readonly IGrainFactory _grainFactory;
    private readonly DurableDeliveryMode _durableDeliveryMode;
    private readonly int _reminderThresholdMs;

    public OrleansActorRuntimeDurableCallbackScheduler(
        IGrainFactory grainFactory,
        AevatarOrleansRuntimeOptions? options = null)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
        var runtimeOptions = options ?? new AevatarOrleansRuntimeOptions();
        _durableDeliveryMode = ParseDurableDeliveryMode(runtimeOptions.RuntimeCallbackDedicatedDeliveryMode);
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

        if (lease.Backend != RuntimeCallbackBackend.Dedicated)
        {
            throw new InvalidOperationException(
                $"Durable Orleans callback scheduler cannot cancel backend '{lease.Backend}'.");
        }

        return CancelDedicatedCallbackAsync(lease.ActorId, lease.CallbackId, lease.Generation);
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
        return _durableDeliveryMode switch
        {
            DurableDeliveryMode.Timer => RuntimeCallbackDeliveryMode.Timer,
            DurableDeliveryMode.Reminder => RuntimeCallbackDeliveryMode.Reminder,
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

    private static DurableDeliveryMode ParseDurableDeliveryMode(string mode)
    {
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.RuntimeCallbackDedicatedDeliveryModeTimer, StringComparison.OrdinalIgnoreCase))
            return DurableDeliveryMode.Timer;
        if (string.Equals(mode, AevatarOrleansRuntimeOptions.RuntimeCallbackDedicatedDeliveryModeReminder, StringComparison.OrdinalIgnoreCase))
            return DurableDeliveryMode.Reminder;
        return DurableDeliveryMode.Auto;
    }

    private static int ToPositiveMilliseconds(TimeSpan value)
    {
        var millis = checked((long)Math.Ceiling(value.TotalMilliseconds));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(millis, 0);
        if (millis > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Duration exceeds supported Orleans callback range.");
        return (int)millis;
    }

    private static void ValidateRequest(
        string actorId,
        string callbackId,
        EventEnvelope triggerEnvelope,
        TimeSpan dueTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentNullException.ThrowIfNull(triggerEnvelope);
        ArgumentNullException.ThrowIfNull(triggerEnvelope.Payload);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTime, TimeSpan.Zero);
    }
}
