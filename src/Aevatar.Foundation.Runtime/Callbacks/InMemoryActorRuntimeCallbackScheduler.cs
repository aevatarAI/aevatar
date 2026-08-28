using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Callbacks;

public sealed class InMemoryActorRuntimeCallbackScheduler :
    IActorRuntimeCallbackScheduler,
    IRuntimeFleetReconcileScheduleOwner,
    IRuntimeFleetReconcileDeliveryVerifier,
    IDisposable
{
    private const int FleetReconcileSlotEpoch = 1;
    private static readonly TimeSpan FleetReconcileInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FleetReconcilePeriod = TimeSpan.FromSeconds(10);
    private readonly IStreamProvider _streams;
    private readonly ConcurrentDictionary<CallbackKey, ScheduledCallback> _callbacks = [];
    private readonly ConcurrentDictionary<CallbackKey, long> _callbackGenerations = [];
    private readonly Lock _coalescingGate = new();
    private readonly Dictionary<CoalescingKey, CoalescingWatermark> _coalescingWatermarks = [];
    private readonly ICollection<KeyValuePair<CallbackKey, ScheduledCallback>> _callbackEntries;

    public InMemoryActorRuntimeCallbackScheduler(IStreamProvider streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _callbackEntries = _callbacks;
    }

    public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfGenericMutationTargetsReservedSlot(request.ActorId, request.CallbackId);
        ValidateScheduleRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ct.ThrowIfCancellationRequested();

        if (request.CoalescingCursor != null)
            return ScheduleCoalescedTimeout(request, request.CoalescingCursor);

        ThrowIfGenericScheduleTargetsCoalescedSlot(request.ActorId, request.CallbackId);

        var key = new CallbackKey(request.ActorId, request.CallbackId);
        var generation = _callbackGenerations.AddOrUpdate(key, 1, (_, current) => current + 1);
        var callback = _callbacks.AddOrUpdate(
            key,
            _ => ScheduledCallback.Create(
                request.ActorId,
                request.CallbackId,
                request.TriggerEnvelope.Clone(),
                request.DeliveryMode,
                isPeriodic: false,
                TimeSpan.Zero,
                generation),
            (_, existing) => existing.Replace(
                request.TriggerEnvelope.Clone(),
                request.DeliveryMode,
                isPeriodic: false,
                TimeSpan.Zero,
                generation));

        callback.Start(this, request.DueTime);
        return Task.FromResult(new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            callback.Generation,
            RuntimeCallbackBackend.InMemory));
    }

    private Task<RuntimeCallbackLease> ScheduleCoalescedTimeout(
        RuntimeCallbackTimeoutRequest request,
        RuntimeEnvelopeRetryCoalescingCursor cursor)
    {
        ScheduledCallback? callbackToStart = null;
        long generation;
        lock (_coalescingGate)
        {
            var coalescingKey = new CoalescingKey(request.ActorId, cursor.Key);
            _coalescingWatermarks.TryGetValue(coalescingKey, out var watermark);
            if (watermark != null &&
                !string.Equals(watermark.CallbackId, request.CallbackId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Coalescing key '{cursor.Key}' is already bound to callback '{watermark.CallbackId}'.");
            }

            if (watermark != null && cursor.Sequence < watermark.Sequence)
            {
                generation = watermark.Generation;
            }
            else
            {
                var callbackKey = new CallbackKey(request.ActorId, request.CallbackId);
                if (watermark != null &&
                    cursor.Sequence == watermark.Sequence &&
                    _callbacks.TryGetValue(callbackKey, out var pending) &&
                    !pending.HasFired)
                {
                    if (pending.CoalescingCursor != cursor || pending.IsPeriodic)
                    {
                        throw new InvalidOperationException(
                            $"Pending callback '{request.CallbackId}' does not match its coalescing watermark.");
                    }

                    generation = pending.Generation;
                }
                else
                {
                    if (_callbacks.TryGetValue(callbackKey, out var existing) &&
                        existing.CoalescingCursor?.Key != cursor.Key)
                    {
                        throw new InvalidOperationException(
                            $"Callback '{request.CallbackId}' is already owned by another schedule.");
                    }

                    generation = _callbackGenerations.AddOrUpdate(
                        callbackKey,
                        1,
                        (_, current) => current + 1);
                    callbackToStart = _callbacks.AddOrUpdate(
                        callbackKey,
                        _ => ScheduledCallback.Create(
                            request.ActorId,
                            request.CallbackId,
                            request.TriggerEnvelope.Clone(),
                            request.DeliveryMode,
                            isPeriodic: false,
                            TimeSpan.Zero,
                            generation,
                            coalescingCursor: cursor),
                        (_, current) => current.Replace(
                            request.TriggerEnvelope.Clone(),
                            request.DeliveryMode,
                            isPeriodic: false,
                            TimeSpan.Zero,
                            generation,
                            coalescingCursor: cursor));
                    _coalescingWatermarks[coalescingKey] = new CoalescingWatermark(
                        request.CallbackId,
                        cursor.Sequence,
                        generation);
                }
            }

            // Start while the coalescing gate still owns the slot. Otherwise a newer sequence
            // can replace an as-yet-unstarted callback, after which the stale caller could start
            // it outside the gate and deliver the superseded envelope.
            callbackToStart?.Start(this, request.DueTime);
        }

        return Task.FromResult(new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            generation,
            RuntimeCallbackBackend.InMemory));
    }

    public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeCallbackTimerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfGenericMutationTargetsReservedSlot(request.ActorId, request.CallbackId);
        ValidateScheduleRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Period, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();
        ThrowIfGenericScheduleTargetsCoalescedSlot(request.ActorId, request.CallbackId);

        var key = new CallbackKey(request.ActorId, request.CallbackId);
        var generation = _callbackGenerations.AddOrUpdate(key, 1, (_, current) => current + 1);
        var callback = _callbacks.AddOrUpdate(
            key,
            _ => ScheduledCallback.Create(
                request.ActorId,
                request.CallbackId,
                request.TriggerEnvelope.Clone(),
                request.DeliveryMode,
                isPeriodic: true,
                request.Period,
                generation),
            (_, existing) => existing.Replace(
                request.TriggerEnvelope.Clone(),
                request.DeliveryMode,
                isPeriodic: true,
                request.Period,
                generation));

        callback.Start(this, request.DueTime);
        return Task.FromResult(new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            callback.Generation,
            RuntimeCallbackBackend.InMemory));
    }

    public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ct.ThrowIfCancellationRequested();
        if (lease.Backend != RuntimeCallbackBackend.InMemory)
            throw new InvalidOperationException($"In-memory callback scheduler cannot cancel backend '{lease.Backend}'.");

        ThrowIfGenericMutationTargetsReservedSlot(lease.ActorId, lease.CallbackId);

        var key = new CallbackKey(lease.ActorId, lease.CallbackId);
        if (!_callbacks.TryGetValue(key, out var callback))
            return Task.CompletedTask;

        if (callback.Generation != lease.Generation)
            return Task.CompletedTask;

        if (!_callbackEntries.Remove(new KeyValuePair<CallbackKey, ScheduledCallback>(key, callback)))
            return Task.CompletedTask;

        callback.Stop();

        return Task.CompletedTask;
    }

    public Task PurgeActorAsync(string actorId, CancellationToken ct = default)
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

        var callbacks = _callbacks
            .Where(x => string.Equals(x.Key.ActorId, actorId, StringComparison.Ordinal))
            .ToList();

        foreach (var entry in callbacks)
        {
            if (_callbackEntries.Remove(new KeyValuePair<CallbackKey, ScheduledCallback>(entry.Key, entry.Value)))
                entry.Value.Stop();
        }

        var generations = _callbackGenerations.Keys
            .Where(x => string.Equals(x.ActorId, actorId, StringComparison.Ordinal))
            .ToList();
        foreach (var key in generations)
            _callbackGenerations.TryRemove(key, out _);

        lock (_coalescingGate)
        {
            var coalescingKeys = _coalescingWatermarks.Keys
                .Where(x => string.Equals(x.ActorId, actorId, StringComparison.Ordinal))
                .ToArray();
            foreach (var key in coalescingKeys)
                _coalescingWatermarks.Remove(key);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var callback in _callbacks.Values)
            callback.Stop();
        _callbacks.Clear();
        _callbackGenerations.Clear();
        lock (_coalescingGate)
            _coalescingWatermarks.Clear();
        GC.SuppressFinalize(this);
    }

    public Task EnsureScheduledAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var actorId = RuntimeFleetCapabilityAuthorityIdentity.ActorId;
        var callbackId = RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId;
        var key = new CallbackKey(actorId, callbackId);
        if (_callbacks.TryGetValue(key, out var existing) && existing.IsFleetReconcile)
            return Task.CompletedTask;

        var generation = _callbackGenerations.AddOrUpdate(key, 1, (_, current) => current + 1);
        var trigger = CreateFleetReconcileTriggerEnvelope();
        var callback = _callbacks.AddOrUpdate(
            key,
            _ => ScheduledCallback.Create(
                actorId,
                callbackId,
                trigger,
                RuntimeCallbackDeliveryMode.FiredSelfEvent,
                isPeriodic: true,
                FleetReconcilePeriod,
                generation,
                isFleetReconcile: true),
            (_, current) => current.Replace(
                trigger,
                RuntimeCallbackDeliveryMode.FiredSelfEvent,
                isPeriodic: true,
                FleetReconcilePeriod,
                generation,
                isFleetReconcile: true));
        callback.Start(this, FleetReconcileInitialDelay);
        return Task.CompletedTask;
    }

    public Task AcknowledgeDeliveryAsync(
        RuntimeFleetReconcileDeliveryAttestation attestation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ct.ThrowIfCancellationRequested();
        if (!_callbacks.TryGetValue(
                new CallbackKey(
                    RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                    RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId),
                out var scheduled) ||
            !scheduled.IsFleetReconcile ||
            !scheduled.TryAcknowledge(attestation))
        {
            throw new InvalidOperationException(
                "Runtime fleet reconcile acknowledgement does not match scheduler-owned delivery state.");
        }

        return Task.CompletedTask;
    }

    public Task<RuntimeFleetReconcileDeliveryAttestation?> VerifyAsync(
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();
        if (envelope.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) != true ||
            envelope.Runtime?.Callback is not { } callback ||
            !string.Equals(
                callback.CallbackId,
                RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(envelope.Id) ||
            !_callbacks.TryGetValue(
                new CallbackKey(
                    RuntimeFleetCapabilityAuthorityIdentity.ActorId,
                    RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId),
                out var scheduled) ||
            !scheduled.IsFleetReconcile ||
            callback.Generation != scheduled.Generation ||
            callback.SlotEpoch != FleetReconcileSlotEpoch)
        {
            return Task.FromResult<RuntimeFleetReconcileDeliveryAttestation?>(null);
        }

        var valid = scheduled.HasExactDelivery(envelope);
        return Task.FromResult<RuntimeFleetReconcileDeliveryAttestation?>(valid
            ? new RuntimeFleetReconcileDeliveryAttestation(
                envelope.Id,
                callback.Generation,
                callback.FireIndex,
                callback.SlotEpoch)
            : null);
    }

    private static void ValidateScheduleRequest(
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

    private void ThrowIfGenericScheduleTargetsCoalescedSlot(
        string actorId,
        string callbackId)
    {
        lock (_coalescingGate)
        {
            if (_coalescingWatermarks.Any(entry =>
                    string.Equals(entry.Key.ActorId, actorId, StringComparison.Ordinal) &&
                    string.Equals(entry.Value.CallbackId, callbackId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Callback '{callbackId}' is owned by the coalesced timeout contract and cannot be replaced by a generic schedule.");
            }
        }
    }

    private static EventEnvelope CreateFleetReconcileTriggerEnvelope() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        Route = EnvelopeRouteSemantics.CreateTopologyPublication(
            RuntimeFleetCapabilityAuthorityIdentity.ActorId,
            TopologyAudience.Self),
        Payload = Any.Pack(new RuntimeFleetReconcileRequested()),
    };

    private async Task OnCallbackFiredAsync(
        CallbackKey key,
        ScheduledCallback callback,
        CancellationToken ct)
    {
        if (!_callbacks.TryGetValue(key, out var current) || !ReferenceEquals(current, callback))
            return;

        var envelope = callback.GetOrCreateDeliveryEnvelope(fireIndex =>
            RuntimeCallbackEnvelopeFactory.CreateScheduledEnvelope(
                callback.ActorId,
                callback.CallbackId,
                callback.Generation,
                fireIndex,
                callback.TriggerEnvelope,
                callback.DeliveryMode,
                callback.IsFleetReconcile
                    ? FleetReconcileSlotEpoch
                    : RuntimeCallbackSlotEpoch.Unspecified));

        await _streams.GetStream(callback.ActorId).ProduceAsync(envelope.Clone(), ct);

        if (!callback.IsPeriodic)
        {
            await CancelAsync(
                new RuntimeCallbackLease(
                    callback.ActorId,
                    callback.CallbackId,
                    callback.Generation,
                    RuntimeCallbackBackend.InMemory),
                CancellationToken.None);
        }
    }

    private readonly record struct CallbackKey(string ActorId, string CallbackId);

    private readonly record struct CoalescingKey(string ActorId, string Key);

    private sealed record CoalescingWatermark(
        string CallbackId,
        long Sequence,
        long Generation);

    private sealed class ScheduledCallback
    {
        private readonly Lock _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _loopTask;
        private long _fireIndex;

        private ScheduledCallback(
            string actorId,
            string callbackId,
            EventEnvelope triggerEnvelope,
            RuntimeCallbackDeliveryMode deliveryMode,
            bool isPeriodic,
            TimeSpan period,
            long generation,
            bool isFleetReconcile = false,
            RuntimeEnvelopeRetryCoalescingCursor? coalescingCursor = null)
        {
            ActorId = actorId;
            CallbackId = callbackId;
            TriggerEnvelope = triggerEnvelope;
            DeliveryMode = deliveryMode;
            IsPeriodic = isPeriodic;
            Period = period;
            Generation = generation;
            IsFleetReconcile = isFleetReconcile;
            CoalescingCursor = coalescingCursor;
        }

        public string ActorId { get; }

        public string CallbackId { get; }

        public EventEnvelope TriggerEnvelope { get; }

        public RuntimeCallbackDeliveryMode DeliveryMode { get; }

        public bool IsPeriodic { get; }

        public TimeSpan Period { get; }

        public long Generation { get; }

        public bool IsFleetReconcile { get; }

        public RuntimeEnvelopeRetryCoalescingCursor? CoalescingCursor { get; }

        public bool HasFired
        {
            get
            {
                lock (_gate)
                    return _fireIndex > 0;
            }
        }

        public EventEnvelope? PendingDeliveryEnvelope { get; private set; }

        public EventEnvelope? LastDeliveryEnvelope { get; private set; }

        public EventEnvelope GetOrCreateDeliveryEnvelope(Func<long, EventEnvelope> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            lock (_gate)
            {
                if (IsFleetReconcile && PendingDeliveryEnvelope != null)
                    return PendingDeliveryEnvelope.Clone();

                var envelope = factory(checked(++_fireIndex));
                if (IsFleetReconcile)
                    PendingDeliveryEnvelope = envelope.Clone();
                return envelope;
            }
        }

        public bool HasExactDelivery(EventEnvelope delivered)
        {
            ArgumentNullException.ThrowIfNull(delivered);
            lock (_gate)
            {
                return HasExactEnvelope(PendingDeliveryEnvelope, delivered) ||
                       HasExactEnvelope(LastDeliveryEnvelope, delivered);
            }
        }

        public bool TryAcknowledge(RuntimeFleetReconcileDeliveryAttestation attestation)
        {
            ArgumentNullException.ThrowIfNull(attestation);
            lock (_gate)
            {
                if (MatchesAttestation(LastDeliveryEnvelope, attestation))
                    return true;
                if (!MatchesAttestation(PendingDeliveryEnvelope, attestation))
                    return false;

                LastDeliveryEnvelope = PendingDeliveryEnvelope!.Clone();
                PendingDeliveryEnvelope = null;
                return true;
            }
        }

        private static bool HasExactEnvelope(EventEnvelope? persisted, EventEnvelope delivered) =>
            persisted != null &&
            persisted.ToByteString().Equals(delivered.ToByteString());

        private static bool MatchesAttestation(
            EventEnvelope? envelope,
            RuntimeFleetReconcileDeliveryAttestation attestation) =>
            envelope?.Payload?.Is(RuntimeFleetReconcileRequested.Descriptor) == true &&
            string.Equals(envelope.Id, attestation.EnvelopeId, StringComparison.Ordinal) &&
            envelope.Runtime?.Callback is { } callback &&
            string.Equals(
                callback.CallbackId,
                RuntimeFleetCapabilityAuthorityIdentity.ReconcileCallbackId,
                StringComparison.Ordinal) &&
            callback.Generation == attestation.Generation &&
            callback.FireIndex == attestation.FireIndex &&
            callback.SlotEpoch == attestation.SlotEpoch;

        public static ScheduledCallback Create(
            string actorId,
            string callbackId,
            EventEnvelope triggerEnvelope,
            RuntimeCallbackDeliveryMode deliveryMode,
            bool isPeriodic,
            TimeSpan period,
            long generation,
            bool isFleetReconcile = false,
            RuntimeEnvelopeRetryCoalescingCursor? coalescingCursor = null)
        {
            return new ScheduledCallback(
                actorId,
                callbackId,
                triggerEnvelope,
                deliveryMode,
                isPeriodic,
                period,
                generation,
                isFleetReconcile,
                coalescingCursor);
        }

        public ScheduledCallback Replace(
            EventEnvelope triggerEnvelope,
            RuntimeCallbackDeliveryMode deliveryMode,
            bool isPeriodic,
            TimeSpan period,
            long generation,
            bool isFleetReconcile = false,
            RuntimeEnvelopeRetryCoalescingCursor? coalescingCursor = null)
        {
            Stop();
            return new ScheduledCallback(
                ActorId,
                CallbackId,
                triggerEnvelope,
                deliveryMode,
                isPeriodic,
                period,
                generation,
                isFleetReconcile,
                coalescingCursor);
        }

        public void Start(InMemoryActorRuntimeCallbackScheduler owner, TimeSpan dueTime)
        {
            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _cts = cts;
                _loopTask = RunLoopAsync(owner, dueTime, cts.Token);
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts = null;
            lock (_gate)
            {
                cts = _cts;
                _cts = null;
            }

            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            finally
            {
                cts.Dispose();
            }
        }

        private async Task RunLoopAsync(
            InMemoryActorRuntimeCallbackScheduler owner,
            TimeSpan dueTime,
            CancellationToken ct)
        {
            try
            {
                await Task.Delay(dueTime, ct);
                await owner.OnCallbackFiredAsync(new CallbackKey(ActorId, CallbackId), this, ct);

                if (!IsPeriodic)
                    return;

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(Period, ct);
                    await owner.OnCallbackFiredAsync(new CallbackKey(ActorId, CallbackId), this, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
