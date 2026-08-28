using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Grains.Callbacks;

public sealed class RuntimeCallbackSchedulerGrain : Grain, IRuntimeCallbackSchedulerGrain, IRemindable
{
    // Refactor (iter73/cluster-073-durable-callback-runtime-credentials):
    //   Old pattern: durable callback envelope clones full command/chunk payload, may embed transient runtime credentials (reply_token)
    //   New principle: callback payload carries only stable IDs + actor-owned lease keys; actor reconciles from current actor state on fire
    private const string ReminderNamePrefix = "runtime-callback:";
    private const string SchedulerStateName = "runtime-callback-scheduler-v2";
    private const int SchedulerSlotEpoch = RuntimeCallbackSlotEpoch.OrleansSchedulerV2;
    private static readonly TimeSpan OneShotReminderRetryPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FleetReconcileInitialDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan FleetReconcilePeriod = TimeSpan.FromSeconds(10);

    private readonly IPersistentState<RuntimeCallbackSchedulerState> _state;
    private Aevatar.Foundation.Abstractions.IStreamProvider _streams = null!;

    public RuntimeCallbackSchedulerGrain(
        [PersistentState(SchedulerStateName, OrleansRuntimeConstants.RuntimeCallbackSchedulerStorageName)]
        IPersistentState<RuntimeCallbackSchedulerState> state)
    {
        _state = state;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _streams = ServiceProvider.GetRequiredService<Aevatar.Foundation.Abstractions.IStreamProvider>();
        await base.OnActivateAsync(cancellationToken);
        await RecoverPendingReminderUnregistrationsAsync(cancellationToken);
        await RecoverOverdueCallbacksAsync(cancellationToken);
    }

    public async Task<long> ScheduleTimeoutAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        int dueTimeMs,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
    {
        ThrowIfGenericMutationTargetsReservedSlot(callbackId);
        ThrowIfGenericMutationTargetsCoalescedSlot(callbackId);
        ValidateScheduleRequest(callbackId, triggerEnvelope, dueTimeMs);
        await RecoverPendingReminderUnregistrationsAsync();
        var dueTime = TimeSpan.FromMilliseconds(dueTimeMs);
        var nextGeneration = await ResetExistingCallbackAndGetNextGenerationAsync(callbackId);
        await UpsertReminderCallbackAsync(
            callbackId,
            nextGeneration,
            periodic: false,
            periodMs: 0,
            triggerEnvelope,
            dueTime,
            deliveryMode);
        return nextGeneration;
    }

    public async Task<long> ScheduleCoalescedTimeoutAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        int dueTimeMs,
        string coalescingKey,
        long coalescingSequence,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
    {
        var cursor = ResolveRollingUpgradeCoalescingCursor(
            triggerEnvelope,
            coalescingKey,
            coalescingSequence);
        return await ScheduleCoalescedTimeoutAsync(
            callbackId,
            triggerEnvelope,
            dueTimeMs,
            cursor.Key,
            cursor.Sequence,
            cursor.ValueIdentity,
            cursor.Precedence,
            deliveryMode);
    }

    public async Task<long> ScheduleCoalescedTimeoutAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        int dueTimeMs,
        string coalescingKey,
        long coalescingSequence,
        string coalescingValueIdentity,
        int coalescingPrecedence,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
    {
        ThrowIfGenericMutationTargetsReservedSlot(callbackId);
        ValidateScheduleRequest(callbackId, triggerEnvelope, dueTimeMs);
        ValidateCoalescingCursor(
            coalescingKey,
            coalescingSequence,
            coalescingValueIdentity,
            coalescingPrecedence);
        await RecoverPendingReminderUnregistrationsAsync();

        var stateBeforeMutation = _state.State.Clone();
        long generation;
        bool stateChanged;
        try
        {
            stateChanged = TryUpsertCoalescedTimeout(
                _state.State,
                this.GetPrimaryKeyString(),
                callbackId,
                triggerEnvelope,
                dueTimeMs,
                coalescingKey,
                coalescingSequence,
                coalescingValueIdentity,
                coalescingPrecedence,
                deliveryMode,
                DateTimeOffset.UtcNow,
                out generation);
            if (stateChanged)
                await _state.WriteStateAsync();
        }
        catch
        {
            _state.State = stateBeforeMutation;
            // A failed write can have an unknown commit outcome. Turn the scheduler over so the
            // transport retry reloads the durable watermark instead of trusting restored memory.
            DeactivateOnIdle();
            throw;
        }

        if (!stateChanged)
        {
            await EnsurePhysicalOneShotReminderAsync(callbackId);
            return generation;
        }

        // State and its high watermark are committed together before the physical reminder is
        // registered. If registration fails, keep the durable schedule: transport redelivery
        // will retry this call and repair the missing reminder without another state write.
        await this.RegisterOrUpdateReminder(
            BuildReminderName(callbackId),
            TimeSpan.FromMilliseconds(dueTimeMs),
            OneShotReminderRetryPeriod);
        return generation;
    }

    public async Task CompleteCoalescedTimeoutAsync(
        string callbackId,
        string coalescingKey,
        long coalescingSequence,
        string coalescingValueIdentity,
        int coalescingPrecedence)
    {
        ThrowIfGenericMutationTargetsReservedSlot(callbackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ValidateCoalescingCursor(
            coalescingKey,
            coalescingSequence,
            coalescingValueIdentity,
            coalescingPrecedence);
        await RecoverPendingReminderUnregistrationsAsync();

        var stateBeforeMutation = _state.State.Clone();
        bool stateChanged;
        bool reminderUnregistrationRequired;
        try
        {
            stateChanged = TryCompleteCoalescedTimeout(
                _state.State,
                callbackId,
                coalescingKey,
                coalescingSequence,
                coalescingValueIdentity,
                coalescingPrecedence,
                out reminderUnregistrationRequired);
            if (stateChanged)
            {
                if (reminderUnregistrationRequired &&
                    !_state.State.PendingReminderUnregistrations.Contains(
                        callbackId,
                        StringComparer.Ordinal))
                {
                    _state.State.PendingReminderUnregistrations.Add(callbackId);
                }

                await _state.WriteStateAsync();
            }
        }
        catch
        {
            _state.State = stateBeforeMutation;
            DeactivateOnIdle();
            throw;
        }

        if (stateChanged && reminderUnregistrationRequired)
            await RecoverPendingReminderUnregistrationsAsync();
    }

    public async Task<long> ScheduleTimerAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        int dueTimeMs,
        int periodMs,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
    {
        ThrowIfGenericMutationTargetsReservedSlot(callbackId);
        ThrowIfGenericMutationTargetsCoalescedSlot(callbackId);
        ValidateScheduleRequest(callbackId, triggerEnvelope, dueTimeMs);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(periodMs, 0);
        await RecoverPendingReminderUnregistrationsAsync();

        var dueTime = TimeSpan.FromMilliseconds(dueTimeMs);
        var nextGeneration = await ResetExistingCallbackAndGetNextGenerationAsync(callbackId);
        await UpsertReminderCallbackAsync(
            callbackId,
            nextGeneration,
            periodic: true,
            periodMs,
            triggerEnvelope,
            dueTime,
            deliveryMode);
        return nextGeneration;
    }

    public async Task CancelAsync(
        string callbackId,
        long expectedGeneration = 0,
        int expectedSlotEpoch = RuntimeCallbackSlotEpoch.Unspecified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ThrowIfGenericMutationTargetsReservedSlot(callbackId);
        ThrowIfGenericMutationTargetsCoalescedSlot(callbackId);
        await RecoverPendingReminderUnregistrationsAsync();
        if (!_state.State.ReminderCallbacks.TryGetValue(callbackId, out var reminderCallback))
            return;

        if (expectedGeneration > 0 && reminderCallback.Generation != expectedGeneration)
            return;

        if (expectedGeneration > 0 && reminderCallback.SlotEpoch != expectedSlotEpoch)
            return;

        _state.State.ReminderCallbacks.Remove(callbackId);
        await _state.WriteStateAsync();
        await TryUnregisterReminderAsync(callbackId);
    }

    public async Task PurgeAsync()
    {
        if (string.Equals(
                this.GetPrimaryKeyString(),
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The runtime fleet capability authority callback scheduler is runtime-reserved and cannot be purged.");
        }

        var persistedIds = _state.State.PendingReminderUnregistrations
            .Concat(_state.State.ReminderCallbacks.Keys)
            .Concat(_state.State.CallbackGenerations.Keys)
            .Concat(_state.State.CoalescingWatermarks.Values.Select(static x => x.CallbackId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (StagePendingReminderUnregistrations(persistedIds))
            await _state.WriteStateAsync();

        var registeredIds = (await this.GetReminders())
            .Select(static reminder =>
                TryParseReminderName(reminder.ReminderName, out var callbackId)
                    ? callbackId
                    : null)
            .Where(static callbackId => callbackId != null)
            .Cast<string>()
            .ToArray();
        if (StagePendingReminderUnregistrations(registeredIds))
            await _state.WriteStateAsync();

        await RecoverPendingReminderUnregistrationsAsync();

        DeactivateOnIdle();
    }

    public async Task<long> EnsureRuntimeFleetReconcileTimerAsync()
    {
        EnsureFleetAuthoritySchedulerIdentity();
        await RecoverPendingReminderUnregistrationsAsync();

        if (_state.State.ReminderCallbacks.TryGetValue(
                RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
                out var existing) &&
            IsValidFleetReconcileSchedule(existing))
        {
            await EnsurePhysicalFleetReconcileReminderAsync(existing);
            return existing.Generation;
        }

        var generation = await ResetExistingCallbackAndGetNextGenerationAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId);
        await UpsertReminderCallbackAsync(
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
            generation,
            periodic: true,
            checked((int)FleetReconcilePeriod.TotalMilliseconds),
            CreateFleetReconcileTriggerEnvelope(),
            FleetReconcileInitialDelay,
            RuntimeCallbackDeliveryMode.FiredSelfEvent);
        return generation;
    }

    private async Task EnsurePhysicalFleetReconcileReminderAsync(
        RuntimeScheduledCallback existing)
    {
        var reminderName = BuildReminderName(
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId);
        if (await this.GetReminder(reminderName) != null)
            return;

        var remainingMs = existing.NextDueAtUnixTimeMs -
                          DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dueTime = TimeSpan.FromMilliseconds(
            Math.Clamp(remainingMs, 1L, existing.PeriodMillis));
        await this.RegisterOrUpdateReminder(
            reminderName,
            dueTime,
            TimeSpan.FromMilliseconds(existing.PeriodMillis));
    }

    public Task<bool> VerifyRuntimeFleetReconcileDeliveryAsync(EventEnvelope envelope)
    {
        EnsureFleetAuthoritySchedulerIdentity();
        return Task.FromResult(IsExactRuntimeFleetReconcileDelivery(_state.State, envelope));
    }

    public async Task AcknowledgeRuntimeFleetReconcileDeliveryAsync(
        string envelopeId,
        long generation,
        long fireIndex,
        int slotEpoch)
    {
        EnsureFleetAuthoritySchedulerIdentity();
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(generation, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fireIndex, 0);
        if (slotEpoch == RuntimeCallbackSlotEpoch.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(slotEpoch));

        var changed = TryAcknowledgeRuntimeFleetReconcileDelivery(
            _state.State,
            envelopeId,
            generation,
            fireIndex,
            slotEpoch,
            out var recognized);
        if (!recognized)
        {
            throw new InvalidOperationException(
                "Runtime fleet reconcile acknowledgement does not match scheduler-owned delivery state.");
        }

        if (changed)
            await _state.WriteStateAsync();
    }

    internal static bool IsExactRuntimeFleetReconcileDelivery(
        RuntimeCallbackSchedulerState state,
        EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) != true ||
            string.IsNullOrWhiteSpace(envelope.Id) ||
            envelope.Runtime?.Callback is not { } callback ||
            !string.Equals(
                callback.CallbackId,
                RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
                StringComparison.Ordinal) ||
            !state.ReminderCallbacks.TryGetValue(
                RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
                out var scheduled) ||
            scheduled.Generation != callback.Generation ||
            scheduled.SlotEpoch != callback.SlotEpoch)
        {
            return false;
        }

        return HasExactEnvelope(scheduled.PendingDeliveryEnvelope, envelope) ||
               HasExactEnvelope(scheduled.LastDeliveryEnvelope, envelope);
    }

    private static bool HasExactEnvelope(EventEnvelope? persisted, EventEnvelope delivered) =>
        persisted != null &&
        persisted.ToByteString().Equals(delivered.ToByteString());

    internal static bool TryAcknowledgeRuntimeFleetReconcileDelivery(
        RuntimeCallbackSchedulerState state,
        string envelopeId,
        long generation,
        long fireIndex,
        int slotEpoch,
        out bool recognized)
    {
        ArgumentNullException.ThrowIfNull(state);
        recognized = false;
        if (!state.ReminderCallbacks.TryGetValue(
                RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
                out var scheduled) ||
            scheduled.Generation != generation ||
            scheduled.SlotEpoch != slotEpoch)
        {
            return false;
        }

        if (MatchesFleetReconcileAttestation(
                scheduled.LastDeliveryEnvelope,
                envelopeId,
                generation,
                fireIndex,
                slotEpoch))
        {
            recognized = true;
            return false;
        }

        var pending = scheduled.PendingDeliveryEnvelope;
        if (!MatchesFleetReconcileAttestation(
                pending,
                envelopeId,
                generation,
                fireIndex,
                slotEpoch))
        {
            return false;
        }

        recognized = true;
        scheduled.FireIndex = fireIndex;
        scheduled.LastDeliveryEnvelopeId = envelopeId;
        scheduled.LastDeliveryFireIndex = fireIndex;
        scheduled.LastDeliveryEnvelope = pending!.Clone();
        scheduled.PendingDeliveryEnvelope = null;
        state.ReminderCallbacks[RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId] = scheduled;
        return true;
    }

    private static bool MatchesFleetReconcileAttestation(
        EventEnvelope? envelope,
        string envelopeId,
        long generation,
        long fireIndex,
        int slotEpoch) =>
        envelope?.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) == true &&
        string.Equals(envelope.Id, envelopeId, StringComparison.Ordinal) &&
        envelope.Runtime?.Callback is { } callback &&
        string.Equals(
            callback.CallbackId,
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
            StringComparison.Ordinal) &&
        callback.Generation == generation &&
        callback.FireIndex == fireIndex &&
        callback.SlotEpoch == slotEpoch;

    private bool StagePendingReminderUnregistrations(IEnumerable<string> callbackIds)
    {
        var pending = _state.State.PendingReminderUnregistrations
            .Concat(callbackIds)
            .Where(static callbackId => !string.IsNullOrWhiteSpace(callbackId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var changed =
            !_state.State.PendingReminderUnregistrations.SequenceEqual(
                pending,
                StringComparer.Ordinal) ||
            _state.State.ReminderCallbacks.Count != 0 ||
            _state.State.CallbackGenerations.Count != 0 ||
            _state.State.CoalescingWatermarks.Count != 0;
        if (!changed)
            return false;

        _state.State.PendingReminderUnregistrations.Clear();
        _state.State.PendingReminderUnregistrations.Add(pending);
        _state.State.ReminderCallbacks.Clear();
        _state.State.CallbackGenerations.Clear();
        _state.State.CoalescingWatermarks.Clear();
        return true;
    }

    private async Task RecoverPendingReminderUnregistrationsAsync(
        CancellationToken ct = default)
    {
        if (_state.State.PendingReminderUnregistrations.Count == 0)
            return;

        var pending = _state.State.PendingReminderUnregistrations.ToArray();
        foreach (var callbackId in pending)
        {
            ct.ThrowIfCancellationRequested();
            await TryUnregisterReminderAsync(callbackId);
        }

        _state.State.PendingReminderUnregistrations.Clear();
        try
        {
            await _state.WriteStateAsync();
        }
        catch
        {
            _state.State.PendingReminderUnregistrations.Add(pending);
            throw;
        }
    }

    private static void ValidateScheduleRequest(string callbackId, EventEnvelope triggerEnvelope, int dueTimeMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentNullException.ThrowIfNull(triggerEnvelope);
        ArgumentNullException.ThrowIfNull(triggerEnvelope.Payload);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTimeMs, 0);
        DurableCallbackEnvelopeCredentialGuard.ThrowIfContainsRuntimeCredential(triggerEnvelope);
    }

    private static void ValidateCoalescingCursor(
        string coalescingKey,
        long coalescingSequence,
        string coalescingValueIdentity,
        int coalescingPrecedence)
    {
        _ = new RuntimeEnvelopeRetryCoalescingCursor(
            coalescingKey,
            coalescingSequence,
            coalescingValueIdentity,
            coalescingPrecedence);
    }

    internal static RuntimeEnvelopeRetryCoalescingCursor ResolveRollingUpgradeCoalescingCursor(
        EventEnvelope triggerEnvelope,
        string coalescingKey,
        long coalescingSequence)
    {
        ArgumentNullException.ThrowIfNull(triggerEnvelope);
        var payload = triggerEnvelope.Payload ??
                      throw new InvalidOperationException(
                          "A rolling-upgrade coalesced retry is missing its committed payload.");

        if (payload.Is(CommittedStateEventPublished.Descriptor))
        {
            var published = payload.Unpack<CommittedStateEventPublished>();
            var stateEvent = published.StateEvent ??
                             throw new InvalidOperationException(
                                 "A rolling-upgrade coalesced retry is missing its committed state event.");
            if (stateEvent.Version != coalescingSequence)
            {
                throw new InvalidOperationException(
                    $"Rolling-upgrade coalescing sequence {coalescingSequence} does not match committed version {stateEvent.Version}.");
            }

            return new RuntimeEnvelopeRetryCoalescingCursor(
                coalescingKey,
                coalescingSequence,
                RuntimeEnvelopeRetryCoalescingValueIdentity.Create(published),
                CommittedStateRepublish.IsRepublishEventId(stateEvent.EventId) ? 1 : 0);
        }

        return new RuntimeEnvelopeRetryCoalescingCursor(
            coalescingKey,
            coalescingSequence,
            RuntimeEnvelopeRetryCoalescingValueIdentity.Create(payload));
    }

    private void ThrowIfGenericMutationTargetsCoalescedSlot(string callbackId)
    {
        if (_state.State.CoalescingWatermarks.Values.Any(
                watermark => string.Equals(
                    watermark.CallbackId,
                    callbackId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Callback '{callbackId}' is owned by the coalesced timeout contract and cannot be mutated through the generic callback API.");
        }
    }

    private void ThrowIfGenericMutationTargetsReservedSlot(string callbackId)
    {
        if (!string.Equals(
                callbackId,
                RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(
                this.GetPrimaryKeyString(),
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The runtime fleet reconcile callback is runtime-reserved and cannot be mutated through the generic callback API.");
        }
    }

    private void EnsureFleetAuthoritySchedulerIdentity()
    {
        if (!string.Equals(
                this.GetPrimaryKeyString(),
                RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The protected fleet reconcile operation is only valid for the fixed fleet authority scheduler.");
        }
    }

    private bool IsFleetReconcileSlot(string callbackId) =>
        string.Equals(
            this.GetPrimaryKeyString(),
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            StringComparison.Ordinal) &&
        string.Equals(
            callbackId,
            RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
            StringComparison.Ordinal);

    private static bool IsValidFleetReconcileSchedule(RuntimeScheduledCallback callback) =>
        callback.Periodic &&
        callback.Generation > 0 &&
        callback.SlotEpoch == SchedulerSlotEpoch &&
        callback.PeriodMillis == checked((int)FleetReconcilePeriod.TotalMilliseconds) &&
        callback.DeliveryMode == RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent &&
        callback.TriggerEnvelope?.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) == true;

    private static EventEnvelope CreateFleetReconcileTriggerEnvelope() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            TopologyAudience.Self),
        Payload = Any.Pack(new RuntimeFleetReconcileRequested()),
    };

    private async Task<long> ResetExistingCallbackAndGetNextGenerationAsync(string callbackId)
    {
        var generation = _state.State.CallbackGenerations.GetValueOrDefault(callbackId);
        var removedExistingCallback = false;
        if (_state.State.ReminderCallbacks.TryGetValue(callbackId, out var reminderCallback))
        {
            generation = Math.Max(generation, reminderCallback.Generation);
            _state.State.ReminderCallbacks.Remove(callbackId);
            removedExistingCallback = true;
        }

        var nextGeneration = generation + 1;
        _state.State.CallbackGenerations[callbackId] = nextGeneration;

        if (removedExistingCallback)
        {
            await _state.WriteStateAsync();
            await TryUnregisterReminderAsync(callbackId);
        }

        return nextGeneration;
    }

    internal static bool TryUpsertCoalescedTimeout(
        RuntimeCallbackSchedulerState state,
        string actorId,
        string callbackId,
        EventEnvelope triggerEnvelope,
        int dueTimeMs,
        string coalescingKey,
        long coalescingSequence,
        string coalescingValueIdentity,
        int coalescingPrecedence,
        RuntimeCallbackDeliveryMode deliveryMode,
        DateTimeOffset scheduledAtUtc,
        out long generation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ValidateScheduleRequest(callbackId, triggerEnvelope, dueTimeMs);
        var incomingCursor = new RuntimeEnvelopeRetryCoalescingCursor(
            coalescingKey,
            coalescingSequence,
            coalescingValueIdentity,
            coalescingPrecedence);

        state.CoalescingWatermarks.TryGetValue(coalescingKey, out var watermark);
        if (watermark != null &&
            !string.Equals(watermark.CallbackId, callbackId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Coalescing key '{coalescingKey}' is already bound to callback '{watermark.CallbackId}'.");
        }

        var comparison = watermark == null
            ? RuntimeEnvelopeRetryCoalescingComparison.Superseding
            : CompareCoalescingWatermark(
                coalescingKey,
                watermark,
                incomingCursor);
        if (comparison == RuntimeEnvelopeRetryCoalescingComparison.Stale)
        {
            generation = ResolveKnownCoalescedGeneration(state, callbackId, watermark!);
            return false;
        }

        if (comparison == RuntimeEnvelopeRetryCoalescingComparison.Conflict)
        {
            throw new InvalidOperationException(
                $"Coalescing key '{coalescingKey}' received conflicting identities at sequence {coalescingSequence}.");
        }

        if (watermark?.Completed == true &&
            comparison == RuntimeEnvelopeRetryCoalescingComparison.Exact)
        {
            generation = ResolveKnownCoalescedGeneration(state, callbackId, watermark);
            return false;
        }

        if (comparison == RuntimeEnvelopeRetryCoalescingComparison.Exact &&
            state.ReminderCallbacks.TryGetValue(callbackId, out var pending) &&
            pending.PendingDeliveryEnvelope == null)
        {
            EnsurePendingCallbackMatchesCursor(
                pending,
                incomingCursor);
            generation = pending.Generation;
            return false;
        }

        if (state.ReminderCallbacks.TryGetValue(callbackId, out var existing))
        {
            if (string.IsNullOrWhiteSpace(existing.CoalescingKey))
            {
                throw new InvalidOperationException(
                    $"Callback '{callbackId}' is already owned by a non-coalesced schedule.");
            }

            if (!string.Equals(existing.CoalescingKey, coalescingKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Callback '{callbackId}' is already bound to coalescing key '{existing.CoalescingKey}'.");
            }
        }

        var previousGeneration = Math.Max(
            state.CallbackGenerations.GetValueOrDefault(callbackId),
            existing?.Generation ?? 0);
        generation = checked(previousGeneration + 1);
        state.ReminderCallbacks[callbackId] = new RuntimeScheduledCallback
        {
            ActorId = actorId,
            CallbackId = callbackId,
            Generation = generation,
            SlotEpoch = SchedulerSlotEpoch,
            Periodic = false,
            DueTimeMillis = dueTimeMs,
            PeriodMillis = 0,
            FireIndex = 0,
            DeliveryMode = ToProtoDeliveryMode(deliveryMode),
            TriggerEnvelope = triggerEnvelope.Clone(),
            NextDueAtUnixTimeMs = scheduledAtUtc.AddMilliseconds(dueTimeMs).ToUnixTimeMilliseconds(),
            OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
            CoalescingKey = coalescingKey,
            CoalescingSequence = coalescingSequence,
            CoalescingValueIdentity = coalescingValueIdentity,
            CoalescingPrecedence = coalescingPrecedence,
        };
        state.CallbackGenerations[callbackId] = generation;
        state.CoalescingWatermarks[coalescingKey] = new RuntimeCallbackCoalescingWatermark
        {
            CallbackId = callbackId,
            Sequence = coalescingSequence,
            ValueIdentity = coalescingValueIdentity,
            Precedence = coalescingPrecedence,
            Completed = false,
        };
        return true;
    }

    internal static bool TryCompleteCoalescedTimeout(
        RuntimeCallbackSchedulerState state,
        string callbackId,
        string coalescingKey,
        long coalescingSequence,
        string coalescingValueIdentity,
        int coalescingPrecedence,
        out bool reminderUnregistrationRequired)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        var incomingCursor = new RuntimeEnvelopeRetryCoalescingCursor(
            coalescingKey,
            coalescingSequence,
            coalescingValueIdentity,
            coalescingPrecedence);
        reminderUnregistrationRequired = false;

        if (!state.CoalescingWatermarks.TryGetValue(coalescingKey, out var watermark))
        {
            if (state.ReminderCallbacks.ContainsKey(callbackId))
            {
                throw new InvalidOperationException(
                    $"Callback '{callbackId}' has a pending schedule without a coalescing watermark.");
            }

            var generation = Math.Max(
                state.CallbackGenerations.GetValueOrDefault(callbackId),
                1L);
            state.CallbackGenerations[callbackId] = generation;
            state.CoalescingWatermarks[coalescingKey] = new RuntimeCallbackCoalescingWatermark
            {
                CallbackId = callbackId,
                Sequence = coalescingSequence,
                ValueIdentity = coalescingValueIdentity,
                Precedence = coalescingPrecedence,
                Completed = true,
            };
            return true;
        }
        if (!string.Equals(watermark.CallbackId, callbackId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Coalescing key '{coalescingKey}' is already bound to callback '{watermark.CallbackId}'.");
        }

        var comparison = CompareCoalescingWatermark(
            coalescingKey,
            watermark,
            incomingCursor);
        if (comparison == RuntimeEnvelopeRetryCoalescingComparison.Conflict)
        {
            throw new InvalidOperationException(
                $"Coalescing key '{coalescingKey}' received conflicting identities at sequence {coalescingSequence}.");
        }
        if (comparison == RuntimeEnvelopeRetryCoalescingComparison.Stale ||
            (comparison == RuntimeEnvelopeRetryCoalescingComparison.Exact && watermark.Completed))
            return false;

        if (state.ReminderCallbacks.TryGetValue(callbackId, out var pending))
        {
            EnsurePendingCallbackMatchesWatermark(
                pending,
                coalescingKey,
                watermark);

            state.ReminderCallbacks.Remove(callbackId);
            state.CallbackGenerations[callbackId] = Math.Max(
                state.CallbackGenerations.GetValueOrDefault(callbackId),
                pending.Generation);
            reminderUnregistrationRequired = true;
        }

        watermark.Sequence = coalescingSequence;
        watermark.ValueIdentity = coalescingValueIdentity;
        watermark.Precedence = coalescingPrecedence;
        watermark.Completed = true;
        state.CoalescingWatermarks[coalescingKey] = watermark;
        return true;
    }

    private static long ResolveKnownCoalescedGeneration(
        RuntimeCallbackSchedulerState state,
        string callbackId,
        RuntimeCallbackCoalescingWatermark watermark)
    {
        var generation = state.ReminderCallbacks.TryGetValue(callbackId, out var pending)
            ? pending.Generation
            : state.CallbackGenerations.GetValueOrDefault(callbackId);
        if (generation <= 0)
        {
            throw new InvalidOperationException(
                $"Coalescing key for callback '{watermark.CallbackId}' has no durable generation.");
        }

        return generation;
    }

    private static void EnsurePendingCallbackMatchesCursor(
        RuntimeScheduledCallback pending,
        RuntimeEnvelopeRetryCoalescingCursor cursor)
    {
        if (!string.Equals(pending.CoalescingKey, cursor.Key, StringComparison.Ordinal) ||
            pending.CoalescingSequence != cursor.Sequence ||
            !string.Equals(
                pending.CoalescingValueIdentity,
                cursor.ValueIdentity,
                StringComparison.Ordinal) ||
            pending.CoalescingPrecedence != cursor.Precedence ||
            pending.Periodic)
        {
            throw new InvalidOperationException(
                $"Pending callback '{pending.CallbackId}' does not match its coalescing watermark.");
        }
    }

    private static void EnsurePendingCallbackMatchesWatermark(
        RuntimeScheduledCallback pending,
        string coalescingKey,
        RuntimeCallbackCoalescingWatermark watermark)
    {
        if (!string.Equals(pending.CoalescingKey, coalescingKey, StringComparison.Ordinal) ||
            pending.CoalescingSequence != watermark.Sequence ||
            !string.Equals(
                pending.CoalescingValueIdentity,
                watermark.ValueIdentity,
                StringComparison.Ordinal) ||
            pending.CoalescingPrecedence != watermark.Precedence ||
            pending.Periodic)
        {
            throw new InvalidOperationException(
                $"Pending callback '{pending.CallbackId}' does not match its coalescing watermark.");
        }
    }

    private static RuntimeEnvelopeRetryCoalescingCursor ReadCoalescingCursor(
        string coalescingKey,
        RuntimeCallbackCoalescingWatermark watermark) =>
        new(
            coalescingKey,
            watermark.Sequence,
            watermark.ValueIdentity,
            watermark.Precedence);

    private static RuntimeEnvelopeRetryCoalescingComparison CompareCoalescingWatermark(
        string coalescingKey,
        RuntimeCallbackCoalescingWatermark watermark,
        RuntimeEnvelopeRetryCoalescingCursor incoming)
    {
        // Watermarks written by the immediately previous binary carried only key + sequence.
        // Preserve rolling-upgrade safety: lower versions stay fenced, higher versions supersede,
        // and the first equal-version operation upgrades the watermark to the typed value identity.
        if (string.IsNullOrWhiteSpace(watermark.ValueIdentity))
        {
            return incoming.Sequence < watermark.Sequence
                ? RuntimeEnvelopeRetryCoalescingComparison.Stale
                : RuntimeEnvelopeRetryCoalescingComparison.Superseding;
        }

        return RuntimeEnvelopeRetryCoalescingCursor.Compare(
            ReadCoalescingCursor(coalescingKey, watermark),
            incoming);
    }

    private async Task EnsurePhysicalOneShotReminderAsync(string callbackId)
    {
        if (!_state.State.ReminderCallbacks.TryGetValue(callbackId, out var pending) ||
            pending.Periodic ||
            string.IsNullOrWhiteSpace(pending.CoalescingKey) ||
            await this.GetReminder(BuildReminderName(callbackId)) != null)
        {
            return;
        }

        var remainingMs = Math.Max(
            pending.NextDueAtUnixTimeMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            1L);
        await this.RegisterOrUpdateReminder(
            BuildReminderName(callbackId),
            TimeSpan.FromMilliseconds(remainingMs),
            OneShotReminderRetryPeriod);
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        _ = status;
        if (!TryParseReminderName(reminderName, out var callbackId))
            return;

        await FireCallbackAsync(callbackId, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private async Task RecoverOverdueCallbacksAsync(CancellationToken ct)
    {
        if (_state.State.ReminderCallbacks.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var callbackIds = _state.State.ReminderCallbacks
            .Where(static x => !x.Value.Periodic)
            .Where(static x => x.Value.NextDueAtUnixTimeMs > 0)
            .Where(x => x.Value.NextDueAtUnixTimeMs <= now.ToUnixTimeMilliseconds())
            .Where(static x => ShouldDeliverOverdueCallback(x.Value.OverduePolicy))
            .Select(static x => x.Key)
            .ToArray();

        foreach (var callbackId in callbackIds)
        {
            ct.ThrowIfCancellationRequested();
            await FireCallbackAsync(callbackId, now, ct);
        }
    }

    internal static bool TryClearCompletedOneShotCallback(
        RuntimeCallbackSchedulerState state,
        string callbackId,
        RuntimeScheduledCallback firedCallback)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentNullException.ThrowIfNull(firedCallback);

        if (!state.ReminderCallbacks.TryGetValue(callbackId, out var current) ||
            current.Generation != firedCallback.Generation ||
            current.SlotEpoch != firedCallback.SlotEpoch)
        {
            return false;
        }

        state.ReminderCallbacks.Remove(callbackId);
        state.CallbackGenerations[callbackId] = Math.Max(
            state.CallbackGenerations.GetValueOrDefault(callbackId),
            firedCallback.Generation);
        return true;
    }

    private async Task FireCallbackAsync(
        string callbackId,
        DateTimeOffset observedAtUtc,
        CancellationToken ct)
    {
        if (!_state.State.ReminderCallbacks.TryGetValue(callbackId, out var scheduled))
        {
            await TryUnregisterReminderAsync(callbackId);
            return;
        }

        var deliveryEnvelope = scheduled.PendingDeliveryEnvelope;
        if (deliveryEnvelope == null)
        {
            var fireIndex = checked(scheduled.FireIndex + 1);
            deliveryEnvelope = RuntimeCallbackEnvelopeFactory.CreateScheduledEnvelope(
                this.GetPrimaryKeyString(),
                callbackId,
                scheduled.Generation,
                fireIndex,
                scheduled.TriggerEnvelope,
                FromProtoDeliveryMode(scheduled.DeliveryMode),
                scheduled.SlotEpoch);
            scheduled.PendingDeliveryEnvelope = deliveryEnvelope.Clone();
            _state.State.ReminderCallbacks[callbackId] = scheduled;
            await _state.WriteStateAsync();
        }

        await _streams.GetStream(this.GetPrimaryKeyString()).ProduceAsync(
            deliveryEnvelope.Clone(),
            ct);

        // The reserved fleet callback is level-triggered. Keep its exact pending envelope stable
        // until the authority commits and acknowledges it; otherwise a lagging Kafka consumer can
        // only ever see deliveries older than the scheduler's moving one-envelope verification
        // window and the admission gate can never reconcile.
        if (IsFleetReconcileSlot(callbackId))
            return;

        if (!scheduled.Periodic)
        {
            if (TryClearCompletedOneShotCallback(_state.State, callbackId, scheduled))
            {
                await _state.WriteStateAsync();
                await TryUnregisterReminderAsync(callbackId);
            }
            return;
        }

        var deliveredFireIndex = deliveryEnvelope.Runtime?.Callback?.FireIndex ?? 0;
        if (deliveredFireIndex <= scheduled.FireIndex)
        {
            throw new InvalidOperationException(
                $"Persisted callback delivery fire index {deliveredFireIndex} is not newer than {scheduled.FireIndex}.");
        }

        scheduled.FireIndex = deliveredFireIndex;
        scheduled.LastDeliveryEnvelopeId = deliveryEnvelope.Id;
        scheduled.LastDeliveryFireIndex = deliveredFireIndex;
        scheduled.LastDeliveryEnvelope = deliveryEnvelope.Clone();
        scheduled.PendingDeliveryEnvelope = null;
        scheduled.NextDueAtUnixTimeMs = ResolveNextPeriodicDueAtUnixTimeMs(
            scheduled,
            observedAtUtc);
        _state.State.ReminderCallbacks[callbackId] = scheduled;
        await _state.WriteStateAsync();
    }

    private async Task UpsertReminderCallbackAsync(
        string callbackId,
        long generation,
        bool periodic,
        int periodMs,
        EventEnvelope triggerEnvelope,
        TimeSpan dueTime,
        RuntimeCallbackDeliveryMode deliveryMode)
    {
        // Refactor (iter48/issue-879-runtime-callback-persistent-state-not-proto):
        //   Old pattern: Orleans durable callback state stored as hand-written C# class with Dictionary<string, ReminderScheduledCallbackState> and byte[] EnvelopeBytes.
        //   New principle: Durable runtime callback ownership is typed protobuf contract; callback ids, schedule fields, generation, fire index, delivery mode, and trigger envelope are explicit proto fields.
        var reminderName = BuildReminderName(callbackId);
        var nextDueAt = DateTimeOffset.UtcNow.Add(dueTime);
        _state.State.ReminderCallbacks[callbackId] = new RuntimeScheduledCallback
        {
            ActorId = this.GetPrimaryKeyString(),
            CallbackId = callbackId,
            Generation = generation,
            SlotEpoch = SchedulerSlotEpoch,
            Periodic = periodic,
            DueTimeMillis = checked((long)dueTime.TotalMilliseconds),
            PeriodMillis = periodMs,
            FireIndex = 0,
            DeliveryMode = ToProtoDeliveryMode(deliveryMode),
            TriggerEnvelope = triggerEnvelope.Clone(),
            NextDueAtUnixTimeMs = nextDueAt.ToUnixTimeMilliseconds(),
            OverduePolicy = RuntimeCallbackOverduePolicy.Deliver,
        };
        _state.State.CallbackGenerations[callbackId] = generation;
        await _state.WriteStateAsync();

        var period = periodic
            ? TimeSpan.FromMilliseconds(periodMs)
            : OneShotReminderRetryPeriod;
        try
        {
            await this.RegisterOrUpdateReminder(reminderName, dueTime, period);
        }
        catch
        {
            _state.State.ReminderCallbacks.Remove(callbackId);
            await _state.WriteStateAsync();
            throw;
        }
    }

    // Orleans resolves the reminder registry from the ambient grain execution context, which is
    // thread-static and only survives awaits that resume on the activation's task scheduler.
    // The lookup/unregister pair therefore belongs to the grain itself: hosting it in a singleton
    // adapter lets any caller orchestrate two context-bound calls across an await from a thread the
    // activation does not own, which fails at the second call with "non-grain context".
    private async Task TryUnregisterReminderAsync(string callbackId)
    {
        var reminder = await this.GetReminder(BuildReminderName(callbackId));
        if (reminder != null)
            await this.UnregisterReminder(reminder);
    }

    private static string BuildReminderName(string callbackId) =>
        string.Concat(ReminderNamePrefix, callbackId);

    private static bool TryParseReminderName(string reminderName, out string callbackId)
    {
        callbackId = string.Empty;
        if (string.IsNullOrWhiteSpace(reminderName) ||
            !reminderName.StartsWith(ReminderNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        callbackId = reminderName[ReminderNamePrefix.Length..];
        return !string.IsNullOrWhiteSpace(callbackId);
    }

    private static long ResolveNextPeriodicDueAtUnixTimeMs(
        RuntimeScheduledCallback scheduled,
        DateTimeOffset observedAtUtc)
    {
        if (scheduled.PeriodMillis <= 0)
            return observedAtUtc.ToUnixTimeMilliseconds();

        var observedUnixMs = observedAtUtc.ToUnixTimeMilliseconds();
        var nextDueAtUnixMs = scheduled.NextDueAtUnixTimeMs;
        if (nextDueAtUnixMs <= 0)
            nextDueAtUnixMs = observedUnixMs;

        while (nextDueAtUnixMs <= observedUnixMs)
            nextDueAtUnixMs = checked(nextDueAtUnixMs + scheduled.PeriodMillis);

        return nextDueAtUnixMs;
    }

    private static bool ShouldDeliverOverdueCallback(RuntimeCallbackOverduePolicy policy) =>
        policy is RuntimeCallbackOverduePolicy.Unspecified or RuntimeCallbackOverduePolicy.Deliver;

    private static RuntimeCallbackScheduleDeliveryMode ToProtoDeliveryMode(RuntimeCallbackDeliveryMode deliveryMode)
    {
        return deliveryMode switch
        {
            RuntimeCallbackDeliveryMode.FiredSelfEvent => RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent,
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery => RuntimeCallbackScheduleDeliveryMode.EnvelopeRedelivery,
            _ => throw new ArgumentOutOfRangeException(nameof(deliveryMode), deliveryMode, "Unknown callback delivery mode."),
        };
    }

    private static RuntimeCallbackDeliveryMode FromProtoDeliveryMode(RuntimeCallbackScheduleDeliveryMode deliveryMode)
    {
        return deliveryMode switch
        {
            RuntimeCallbackScheduleDeliveryMode.Unspecified => RuntimeCallbackDeliveryMode.FiredSelfEvent,
            RuntimeCallbackScheduleDeliveryMode.FiredSelfEvent => RuntimeCallbackDeliveryMode.FiredSelfEvent,
            RuntimeCallbackScheduleDeliveryMode.EnvelopeRedelivery => RuntimeCallbackDeliveryMode.EnvelopeRedelivery,
            _ => throw new ArgumentOutOfRangeException(nameof(deliveryMode), deliveryMode, "Unknown persisted callback delivery mode."),
        };
    }
}
