using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryEventEnvelopeDeliveryPort : IEventEnvelopeDeliveryPort
{
    private const string ScriptOutputDeliveryKind = "script_output";

    private readonly InMemoryEventEnvelopeBusState _state;

    public InMemoryEventEnvelopeDeliveryPort(InMemoryEventEnvelopeBusState state)
    {
        _state = state;
    }

    public Task<IReadOnlyList<EnvelopeSubscribeRequest>> ListLeasesAsync(string stackId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var leases = _state.Leases.Values
            .Where(item => string.Equals(item.StackId, stackId, StringComparison.Ordinal))
            .OrderBy(item => item.ServiceName, StringComparer.Ordinal)
            .ThenBy(item => item.LeaseId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<EnvelopeSubscribeRequest>>(leases);
    }

    public Task<IReadOnlyList<EnvelopeDeliverySnapshot>> PullAsync(string leaseId, int maxCount, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (maxCount <= 0)
            return Task.FromResult<IReadOnlyList<EnvelopeDeliverySnapshot>>([]);
        if (!_state.Leases.TryGetValue(leaseId, out var lease))
            return Task.FromResult<IReadOnlyList<EnvelopeDeliverySnapshot>>([]);

        var now = DateTime.UtcNow;
        var result = new List<EnvelopeDeliverySnapshot>(maxCount);
        var ordered = _state.Envelopes.OrderBy(item => item.Key, Comparer<long>.Default);
        foreach (var envelopePair in ordered)
        {
            if (result.Count >= maxCount)
                break;

            var sequence = envelopePair.Key;
            var envelope = envelopePair.Value;
            if (!ShouldDeliver(lease, envelope))
                continue;

            var stateKey = BuildStateKey(lease.LeaseId, sequence);
            var deliveryState = _state.DeliveryStates.GetOrAdd(stateKey, _ => new InMemoryEventEnvelopeBusState.LeaseEnvelopeDeliveryState
            {
                FirstSeenAtUtc = now,
                VisibleAtUtc = now,
            });

            if (deliveryState.Acked)
                continue;
            if (deliveryState.Attempt >= InMemoryEventEnvelopeBusState.MaxDeliveryAttempts)
                continue;
            if (deliveryState.InFlight && deliveryState.InFlightDeadlineUtc > now)
                continue;
            if (deliveryState.VisibleAtUtc > now)
                continue;

            deliveryState.InFlight = true;
            deliveryState.Attempt += 1;
            deliveryState.InFlightDeadlineUtc = now.Add(InMemoryEventEnvelopeBusState.InFlightLeaseTimeout);

            var deliveryId = Guid.NewGuid().ToString("N");
            _state.DeliveryPointers[deliveryId] = new InMemoryEventEnvelopeBusState.DeliveryPointer(lease.LeaseId, sequence, deliveryState.Attempt);
            result.Add(new EnvelopeDeliverySnapshot(
                DeliveryId: deliveryId,
                LeaseId: lease.LeaseId,
                Attempt: deliveryState.Attempt,
                Envelope: envelope,
                FirstSeenAtUtc: deliveryState.FirstSeenAtUtc));
        }

        return Task.FromResult<IReadOnlyList<EnvelopeDeliverySnapshot>>(result);
    }

    public Task AckAsync(string leaseId, string deliveryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_state.DeliveryPointers.TryRemove(deliveryId, out var pointer))
            return Task.CompletedTask;
        if (!string.Equals(pointer.LeaseId, leaseId, StringComparison.Ordinal))
            return Task.CompletedTask;

        var stateKey = BuildStateKey(pointer.LeaseId, pointer.Sequence);
        if (_state.DeliveryStates.TryGetValue(stateKey, out var deliveryState))
        {
            deliveryState.Acked = true;
            deliveryState.InFlight = false;
            deliveryState.VisibleAtUtc = DateTime.MaxValue;
        }

        return Task.CompletedTask;
    }

    public Task<EnvelopeRetryResult> RetryAsync(string leaseId, string deliveryId, TimeSpan delay, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_state.DeliveryPointers.TryRemove(deliveryId, out var pointer))
            return Task.FromResult(new EnvelopeRetryResult(false, false, "ENVELOPE_DELIVERY_NOT_FOUND", "delivery pointer not found"));
        if (!string.Equals(pointer.LeaseId, leaseId, StringComparison.Ordinal))
            return Task.FromResult(new EnvelopeRetryResult(false, false, "ENVELOPE_LEASE_INVALID", "lease mismatch"));

        var stateKey = BuildStateKey(pointer.LeaseId, pointer.Sequence);
        if (!_state.DeliveryStates.TryGetValue(stateKey, out var deliveryState))
            return Task.FromResult(new EnvelopeRetryResult(false, false, "ENVELOPE_DELIVERY_NOT_FOUND", "delivery state not found"));

        deliveryState.InFlight = false;
        deliveryState.LastFailureReason = reason ?? string.Empty;
        var boundedDelay = delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(50) : delay;
        deliveryState.VisibleAtUtc = DateTime.UtcNow.Add(boundedDelay);

        if (deliveryState.Attempt >= InMemoryEventEnvelopeBusState.MaxDeliveryAttempts)
        {
            deliveryState.Acked = true;
            return Task.FromResult(new EnvelopeRetryResult(false, true, "ENVELOPE_RETRY_EXHAUSTED", "retry attempts exhausted"));
        }

        return Task.FromResult(new EnvelopeRetryResult(true, false));
    }

    private static bool ShouldDeliver(EnvelopeSubscribeRequest lease, ScriptEventEnvelope envelope)
    {
        if (!string.Equals(lease.StackId, envelope.StackId, StringComparison.Ordinal))
            return false;
        if (string.Equals(lease.ServiceName, envelope.ServiceName, StringComparison.Ordinal))
            return false;

        if (!envelope.Envelope.Metadata.TryGetValue("delivery_kind", out var deliveryKind))
            return false;
        return string.Equals(deliveryKind, ScriptOutputDeliveryKind, StringComparison.Ordinal);
    }

    private static string BuildStateKey(string leaseId, long sequence) => $"{leaseId}:{sequence}";
}
