using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;

namespace Aevatar.Foundation.Runtime.Callbacks;

public sealed class InMemoryActorRuntimeCallbackScheduler :
    IActorRuntimeCallbackScheduler
{
    private readonly IStreamProvider _streams;
    private readonly ConcurrentDictionary<CallbackKey, ScheduledCallback> _callbacks = [];
    private readonly ICollection<KeyValuePair<CallbackKey, ScheduledCallback>> _callbackEntries;

    public InMemoryActorRuntimeCallbackScheduler(IStreamProvider streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _callbackEntries = _callbacks;
    }

    public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeCallbackTimeoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScheduleRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ct.ThrowIfCancellationRequested();

        var key = new CallbackKey(request.ActorId, request.CallbackId);
        var callback = _callbacks.AddOrUpdate(
            key,
            _ => ScheduledCallback.Create(
                request.ActorId,
                request.CallbackId,
                request.TriggerEnvelope.Clone(),
                request.DeliveryMode,
                isPeriodic: false,
                TimeSpan.Zero),
            (_, existing) => existing.Replace(
                request.TriggerEnvelope.Clone(),
                request.DeliveryMode,
                isPeriodic: false,
                TimeSpan.Zero));

        callback.Start(this, request.DueTime);
        return Task.FromResult(new RuntimeCallbackLease(
            request.ActorId,
            request.CallbackId,
            callback.Generation,
            RuntimeCallbackBackend.InMemory));
    }

    public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeCallbackTimerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScheduleRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Period, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var key = new CallbackKey(request.ActorId, request.CallbackId);
        var callback = _callbacks.AddOrUpdate(
            key,
            _ => ScheduledCallback.Create(
                request.ActorId,
                request.CallbackId,
                request.TriggerEnvelope.Clone(),
                request.DeliveryMode,
                isPeriodic: true,
                request.Period),
            (_, existing) => existing.Replace(
                request.TriggerEnvelope.Clone(),
                request.DeliveryMode,
                isPeriodic: true,
                request.Period));

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

        var callbacks = _callbacks
            .Where(x => string.Equals(x.Key.ActorId, actorId, StringComparison.Ordinal))
            .ToList();

        foreach (var entry in callbacks)
        {
            if (_callbackEntries.Remove(new KeyValuePair<CallbackKey, ScheduledCallback>(entry.Key, entry.Value)))
                entry.Value.Stop();
        }

        return Task.CompletedTask;
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

    private async Task OnCallbackFiredAsync(
        CallbackKey key,
        ScheduledCallback callback,
        CancellationToken ct)
    {
        if (!_callbacks.TryGetValue(key, out var current) || !ReferenceEquals(current, callback))
            return;

        var fireIndex = callback.IncrementFireIndex();
        var envelope = RuntimeCallbackEnvelopeFactory.CreateScheduledEnvelope(
            callback.ActorId,
            callback.CallbackId,
            callback.Generation,
            fireIndex,
            callback.TriggerEnvelope,
            callback.DeliveryMode);

        await _streams.GetStream(callback.ActorId).ProduceAsync(envelope, ct);

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
            long generation)
        {
            ActorId = actorId;
            CallbackId = callbackId;
            TriggerEnvelope = triggerEnvelope;
            DeliveryMode = deliveryMode;
            IsPeriodic = isPeriodic;
            Period = period;
            Generation = generation;
        }

        public string ActorId { get; }

        public string CallbackId { get; }

        public EventEnvelope TriggerEnvelope { get; }

        public RuntimeCallbackDeliveryMode DeliveryMode { get; }

        public bool IsPeriodic { get; }

        public TimeSpan Period { get; }

        public long Generation { get; }

        public static ScheduledCallback Create(
            string actorId,
            string callbackId,
            EventEnvelope triggerEnvelope,
            RuntimeCallbackDeliveryMode deliveryMode,
            bool isPeriodic,
            TimeSpan period)
        {
            return new ScheduledCallback(actorId, callbackId, triggerEnvelope, deliveryMode, isPeriodic, period, generation: 1);
        }

        public ScheduledCallback Replace(
            EventEnvelope triggerEnvelope,
            RuntimeCallbackDeliveryMode deliveryMode,
            bool isPeriodic,
            TimeSpan period)
        {
            Stop();
            return new ScheduledCallback(
                ActorId,
                CallbackId,
                triggerEnvelope,
                deliveryMode,
                isPeriodic,
                period,
                Generation + 1);
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

        public long IncrementFireIndex() => Interlocked.Increment(ref _fireIndex);

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
