using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;

public sealed class OrleansActorRuntimeDurableCallbackScheduler
    : IActorRuntimeCallbackScheduler
{
    private readonly IGrainFactory _grainFactory;

    public OrleansActorRuntimeDurableCallbackScheduler(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
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
            request.DueTime);

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
        TimeSpan dueTime)
    {
        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return await grain.ScheduleTimeoutAsync(
            callbackId,
            envelope.ToByteArray(),
            ToPositiveMilliseconds(dueTime));
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
            ToPositiveMilliseconds(period));
    }

    private Task CancelDedicatedCallbackAsync(
        string actorId,
        string callbackId,
        long expectedGeneration = 0)
    {
        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return grain.CancelAsync(callbackId, expectedGeneration);
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
