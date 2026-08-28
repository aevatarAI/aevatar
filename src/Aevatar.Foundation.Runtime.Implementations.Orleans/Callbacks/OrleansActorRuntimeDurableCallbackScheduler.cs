using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Callbacks;

public sealed class OrleansActorRuntimeDurableCallbackScheduler
    : IActorRuntimeCallbackScheduler,
      IRuntimeEnvelopeRetryCoalescingCallbackScheduler,
      IRuntimeFleetReconcileScheduleOwner,
      IRuntimeFleetReconcileDeliveryVerifier
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
        ThrowIfGenericMutationTargetsReservedSlot(request.ActorId, request.CallbackId);
        ValidateRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ct.ThrowIfCancellationRequested();

        var generation = request.CoalescingCursor == null
            ? await ScheduleViaDedicatedGrainTimeoutAsync(
                request.ActorId,
                request.CallbackId,
                request.TriggerEnvelope,
                request.DueTime,
                request.DeliveryMode)
            : await ScheduleViaDedicatedGrainCoalescedTimeoutAsync(
                request.ActorId,
                request.CallbackId,
                request.TriggerEnvelope,
                request.DueTime,
                request.CoalescingCursor,
                request.DeliveryMode);

        return new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            generation,
            RuntimeCallbackBackend.Dedicated)
        {
            SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
        };
    }

    public async Task<RuntimeCallbackLease> ScheduleTimerAsync(
        RuntimeCallbackTimerRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfGenericMutationTargetsReservedSlot(request.ActorId, request.CallbackId);
        ValidateRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Period, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var generation = await ScheduleViaDedicatedGrainTimerAsync(
            request.ActorId,
            request.CallbackId,
            request.TriggerEnvelope,
            request.DueTime,
            request.Period,
            request.DeliveryMode);

        return new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            generation,
            RuntimeCallbackBackend.Dedicated)
        {
            SlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2,
        };
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

        ThrowIfGenericMutationTargetsReservedSlot(lease.ActorId, lease.CallbackId);

        return CancelDedicatedCallbackAsync(lease.ActorId, lease.CallbackId, lease.Generation, lease.SlotEpoch);
    }

    public Task PurgeActorAsync(
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ct.ThrowIfCancellationRequested();
        if (string.Equals(
                actorId,
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The runtime fleet capability authority callback scheduler is runtime-reserved and cannot be purged.");
        }
        return _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId).PurgeAsync();
    }

    public Task CompleteRuntimeEnvelopeRetryAsync(
        string actorId,
        RuntimeEnvelopeRetryCoalescingCursor cursor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(cursor);
        ct.ThrowIfCancellationRequested();
        return _grainFactory
            .GetGrain<IRuntimeCallbackSchedulerGrain>(actorId)
            .CompleteCoalescedTimeoutAsync(
                RuntimeEnvelopeRetryCoalescingCallbackSlot.BuildCallbackId(cursor.Key),
                cursor.Key,
                cursor.Sequence,
                cursor.ValueIdentity,
                cursor.Precedence);
    }

    public async Task EnsureScheduledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _grainFactory
            .GetGrain<IRuntimeCallbackSchedulerGrain>(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId)
            .EnsureRuntimeFleetReconcileTimerAsync();
    }

    public async Task AcknowledgeDeliveryAsync(
        RuntimeFleetReconcileDeliveryAttestation attestation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ct.ThrowIfCancellationRequested();
        await _grainFactory
            .GetGrain<IRuntimeCallbackSchedulerGrain>(
                RuntimeFleetCapabilityAuthorityIdentity.ActorId)
            .AcknowledgeRuntimeFleetReconcileDeliveryAsync(
                attestation.EnvelopeId,
                attestation.Generation,
                attestation.FireIndex,
                attestation.SlotEpoch);
    }

    public async Task<RuntimeFleetReconcileDeliveryAttestation?> VerifyAsync(
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();
        var callback = envelope.Runtime?.Callback;
        if (callback == null ||
            string.IsNullOrWhiteSpace(envelope.Id) ||
            !string.Equals(
                callback.CallbackId,
                RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
                StringComparison.Ordinal) ||
            !await _grainFactory
                .GetGrain<IRuntimeCallbackSchedulerGrain>(
                    RuntimeFleetCapabilityAuthorityIdentity.ActorId)
                .VerifyRuntimeFleetReconcileDeliveryAsync(envelope))
        {
            return null;
        }

        return new RuntimeFleetReconcileDeliveryAttestation(
            envelope.Id,
            callback.Generation,
            callback.FireIndex,
            callback.SlotEpoch);
    }

    private async Task<long> ScheduleViaDedicatedGrainTimeoutAsync(
        string actorId,
        string callbackId,
        EventEnvelope envelope,
        TimeSpan dueTime,
        RuntimeCallbackDeliveryMode deliveryMode)
    {
        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return await grain.ScheduleTimeoutAsync(
            callbackId,
            envelope,
            ToPositiveMilliseconds(dueTime),
            deliveryMode);
    }

    private async Task<long> ScheduleViaDedicatedGrainCoalescedTimeoutAsync(
        string actorId,
        string callbackId,
        EventEnvelope envelope,
        TimeSpan dueTime,
        RuntimeEnvelopeRetryCoalescingCursor coalescingCursor,
        RuntimeCallbackDeliveryMode deliveryMode)
    {
        var rollingUpgradeCursor = RuntimeCallbackSchedulerGrain.ResolveRollingUpgradeCoalescingCursor(
            envelope,
            coalescingCursor.Key,
            coalescingCursor.Sequence);
        if (rollingUpgradeCursor != coalescingCursor)
        {
            throw new InvalidOperationException(
                "The rolling-upgrade coalesced timeout wire contract cannot preserve the supplied typed cursor.");
        }

        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        // Keep the wire call on the previous signature for this rollout. A new runtime can then
        // persist the authoritative version fence even when this grain is still activated on an
        // older silo. The new grain derives the typed value identity and maintenance precedence
        // from the exact committed envelope before entering the typed implementation.
        return await grain.ScheduleCoalescedTimeoutAsync(
            callbackId,
            envelope,
            ToPositiveMilliseconds(dueTime),
            coalescingCursor.Key,
            coalescingCursor.Sequence,
            deliveryMode);
    }

    private async Task<long> ScheduleViaDedicatedGrainTimerAsync(
        string actorId,
        string callbackId,
        EventEnvelope envelope,
        TimeSpan dueTime,
        TimeSpan period,
        RuntimeCallbackDeliveryMode deliveryMode)
    {
        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return await grain.ScheduleTimerAsync(
            callbackId,
            envelope,
            ToPositiveMilliseconds(dueTime),
            ToPositiveMilliseconds(period),
            deliveryMode);
    }

    private Task CancelDedicatedCallbackAsync(
        string actorId,
        string callbackId,
        long expectedGeneration = 0,
        int expectedSlotEpoch = RuntimeCallbackSlotEpoch.Unspecified)
    {
        var grain = _grainFactory.GetGrain<IRuntimeCallbackSchedulerGrain>(actorId);
        return grain.CancelAsync(callbackId, expectedGeneration, expectedSlotEpoch);
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

    private static void ThrowIfGenericMutationTargetsReservedSlot(
        string actorId,
        string callbackId)
    {
        if (RuntimeFleetCapabilityAuthorityIdentity.IsReservedCallback(actorId, callbackId))
        {
            throw new InvalidOperationException(
                "The runtime fleet reconcile callback is runtime-reserved and cannot be mutated through the generic callback API.");
        }
    }
}
