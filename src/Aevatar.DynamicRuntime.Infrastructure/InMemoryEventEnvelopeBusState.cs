using System.Collections.Concurrent;
using Aevatar.DynamicRuntime.Abstractions.Contracts;

namespace Aevatar.DynamicRuntime.Infrastructure;

public sealed class InMemoryEventEnvelopeBusState
{
    public const int MaxDeliveryAttempts = 8;
    public static readonly TimeSpan InFlightLeaseTimeout = TimeSpan.FromSeconds(30);

    public ConcurrentDictionary<string, EnvelopeSubscribeRequest> Leases { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<long, ScriptEventEnvelope> Envelopes { get; } = new();
    public ConcurrentDictionary<string, LeaseEnvelopeDeliveryState> DeliveryStates { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, DeliveryPointer> DeliveryPointers { get; } = new(StringComparer.Ordinal);

    private long _sequence;

    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public sealed class LeaseEnvelopeDeliveryState
    {
        public int Attempt { get; set; }
        public bool Acked { get; set; }
        public bool InFlight { get; set; }
        public DateTime FirstSeenAtUtc { get; set; }
        public DateTime VisibleAtUtc { get; set; }
        public DateTime InFlightDeadlineUtc { get; set; }
        public string LastFailureReason { get; set; } = string.Empty;
    }

    public sealed record DeliveryPointer(string LeaseId, long Sequence, int Attempt);
}
