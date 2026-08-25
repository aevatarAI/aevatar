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

    public async Task<long> ScheduleTimerAsync(
        string callbackId,
        EventEnvelope triggerEnvelope,
        int dueTimeMs,
        int periodMs,
        RuntimeCallbackDeliveryMode deliveryMode = RuntimeCallbackDeliveryMode.FiredSelfEvent)
    {
        ThrowIfGenericMutationTargetsReservedSlot(callbackId);
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
            _state.State.CallbackGenerations.Count != 0;
        if (!changed)
            return false;

        _state.State.PendingReminderUnregistrations.Clear();
        _state.State.PendingReminderUnregistrations.Add(pending);
        _state.State.ReminderCallbacks.Clear();
        _state.State.CallbackGenerations.Clear();
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
