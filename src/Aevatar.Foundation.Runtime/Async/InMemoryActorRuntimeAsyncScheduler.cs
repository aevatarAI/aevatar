using System.Collections.Concurrent;
using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Async;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Async;

public sealed class InMemoryActorRuntimeAsyncScheduler : IActorRuntimeAsyncScheduler
{
    private readonly IStreamProvider _streams;
    private readonly ConcurrentDictionary<string, ScheduledCallback> _callbacks = new(StringComparer.Ordinal);

    public InMemoryActorRuntimeAsyncScheduler(IStreamProvider streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
    }

    public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(RuntimeTimeoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScheduleRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ct.ThrowIfCancellationRequested();

        var key = BuildKey(request.ActorId, request.CallbackId);
        var callback = _callbacks.AddOrUpdate(
            key,
            _ => ScheduledCallback.Create(request.ActorId, request.CallbackId, request.TriggerEnvelope.Clone(), isPeriodic: false, TimeSpan.Zero),
            (_, existing) => existing.Replace(request.TriggerEnvelope.Clone(), isPeriodic: false, TimeSpan.Zero));

        callback.Start(this, request.DueTime);
        return Task.FromResult(new RuntimeCallbackLease(request.ActorId, request.CallbackId, callback.Generation));
    }

    public Task<RuntimeCallbackLease> ScheduleTimerAsync(RuntimeTimerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScheduleRequest(request.ActorId, request.CallbackId, request.TriggerEnvelope, request.DueTime);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Period, TimeSpan.Zero);
        ct.ThrowIfCancellationRequested();

        var key = BuildKey(request.ActorId, request.CallbackId);
        var callback = _callbacks.AddOrUpdate(
            key,
            _ => ScheduledCallback.Create(request.ActorId, request.CallbackId, request.TriggerEnvelope.Clone(), isPeriodic: true, request.Period),
            (_, existing) => existing.Replace(request.TriggerEnvelope.Clone(), isPeriodic: true, request.Period));

        callback.Start(this, request.DueTime);
        return Task.FromResult(new RuntimeCallbackLease(request.ActorId, request.CallbackId, callback.Generation));
    }

    public Task CancelAsync(string actorId, string callbackId, long? expectedGeneration = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ct.ThrowIfCancellationRequested();

        var key = BuildKey(actorId, callbackId);
        if (!_callbacks.TryGetValue(key, out var callback))
            return Task.CompletedTask;

        if (expectedGeneration.HasValue && callback.Generation != expectedGeneration.Value)
            return Task.CompletedTask;

        if (_callbacks.TryRemove(key, out var removed))
            removed.Stop();

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

    private static string BuildKey(string actorId, string callbackId) =>
        string.Concat(actorId, "::", callbackId);

    private async Task OnCallbackFiredAsync(
        string key,
        ScheduledCallback callback,
        CancellationToken ct)
    {
        if (!_callbacks.TryGetValue(key, out var current) || !ReferenceEquals(current, callback))
            return;

        var fireIndex = callback.IncrementFireIndex();
        var envelope = callback.TriggerEnvelope.Clone();
        envelope.Direction = EventDirection.Self;
        envelope.TargetActorId = callback.ActorId;
        envelope.PublisherId = callback.ActorId;
        envelope.Id = Guid.NewGuid().ToString("N");
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackId] = callback.CallbackId;
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackGeneration] = callback.Generation.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFireIndex] = fireIndex.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        await _streams.GetStream(callback.ActorId).ProduceAsync(envelope, ct);

        if (!callback.IsPeriodic)
            await CancelAsync(callback.ActorId, callback.CallbackId, callback.Generation, CancellationToken.None);
    }

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
            bool isPeriodic,
            TimeSpan period,
            long generation)
        {
            ActorId = actorId;
            CallbackId = callbackId;
            TriggerEnvelope = triggerEnvelope;
            IsPeriodic = isPeriodic;
            Period = period;
            Generation = generation;
        }

        public string ActorId { get; }

        public string CallbackId { get; }

        public EventEnvelope TriggerEnvelope { get; }

        public bool IsPeriodic { get; }

        public TimeSpan Period { get; }

        public long Generation { get; }

        public static ScheduledCallback Create(
            string actorId,
            string callbackId,
            EventEnvelope triggerEnvelope,
            bool isPeriodic,
            TimeSpan period)
        {
            return new ScheduledCallback(actorId, callbackId, triggerEnvelope, isPeriodic, period, generation: 1);
        }

        public ScheduledCallback Replace(
            EventEnvelope triggerEnvelope,
            bool isPeriodic,
            TimeSpan period)
        {
            Stop();
            return new ScheduledCallback(
                ActorId,
                CallbackId,
                triggerEnvelope,
                isPeriodic,
                period,
                Generation + 1);
        }

        public void Start(InMemoryActorRuntimeAsyncScheduler owner, TimeSpan dueTime)
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
            InMemoryActorRuntimeAsyncScheduler owner,
            TimeSpan dueTime,
            CancellationToken ct)
        {
            try
            {
                await Task.Delay(dueTime, ct);
                await owner.OnCallbackFiredAsync(BuildKey(ActorId, CallbackId), this, ct);

                if (!IsPeriodic)
                    return;

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(Period, ct);
                    await owner.OnCallbackFiredAsync(BuildKey(ActorId, CallbackId), this, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
