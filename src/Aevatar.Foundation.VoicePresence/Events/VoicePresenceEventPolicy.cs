using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.VoicePresence.Events;

/// <summary>
/// Admission policy for buffered voice event injection.
/// </summary>
public sealed class VoicePresenceEventPolicy
{
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromSeconds(10);

    // Refactor (iter104/cluster-3): Old pattern: VoicePresenceEventPolicy kept module-local in-memory recent-event dedupe set. New principle: dedupe fence in VoicePresenceRuntimeState (actor-owned); policy is pure evaluator over passed-in actor state.
    public VoicePresenceEventPolicyVerdict Evaluate(
        EventEnvelope envelope,
        DateTimeOffset now,
        IEnumerable<VoicePresenceEventDedupeFenceEntry> recentEvents)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(recentEvents);

        var observedAt = envelope.Timestamp?.ToDateTimeOffset() ?? now;
        if (now - observedAt > StaleAfter)
            return VoicePresenceEventPolicyVerdict.Drop(VoicePresenceEventPolicyDecision.DropStale);

        var key = BuildKey(envelope);
        var cutoff = now - DedupeWindow;
        foreach (var entry in recentEvents)
        {
            var recordedAt = entry.RecordedAt?.ToDateTimeOffset();
            if (recordedAt is null || recordedAt < cutoff)
                continue;

            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                return VoicePresenceEventPolicyVerdict.Drop(VoicePresenceEventPolicyDecision.DropDuplicate);
        }

        return VoicePresenceEventPolicyVerdict.Admit(key, Timestamp.FromDateTimeOffset(now));
    }

    public IReadOnlyList<VoicePresenceEventDedupeFenceEntry> BuildFence(
        IEnumerable<VoicePresenceEventDedupeFenceEntry> recentEvents,
        VoicePresenceEventPolicyVerdict verdict,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(recentEvents);

        var cutoff = now - DedupeWindow;
        var pruned = new List<VoicePresenceEventDedupeFenceEntry>();
        foreach (var entry in recentEvents)
        {
            var recordedAt = entry.RecordedAt?.ToDateTimeOffset();
            if (recordedAt is null || recordedAt < cutoff)
                continue;

            pruned.Add(entry.Clone());
        }

        if (verdict.Decision == VoicePresenceEventPolicyDecision.Admit)
        {
            pruned.Add(new VoicePresenceEventDedupeFenceEntry
            {
                Key = verdict.Key,
                RecordedAt = verdict.RecordedAt?.Clone(),
            });
        }

        return pruned;
    }

    public static string BuildKey(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var operationId = envelope.Runtime?.DeliveryIdentity?.OperationId;
        if (!string.IsNullOrWhiteSpace(operationId))
            return $"operation:{operationId.Trim()}";

        if (!string.IsNullOrWhiteSpace(envelope.Id))
            return $"envelope:{envelope.Id.Trim()}";

        if (envelope.Payload == null)
            return "payload:null";

        var payloadBytes = envelope.Payload.Value.IsEmpty
            ? string.Empty
            : Convert.ToBase64String(envelope.Payload.Value.ToByteArray());

        return $"{envelope.Payload.TypeUrl}|{payloadBytes}";
    }
}

public sealed record VoicePresenceEventPolicyVerdict(
    VoicePresenceEventPolicyDecision Decision,
    string Key,
    Timestamp? RecordedAt)
{
    public static VoicePresenceEventPolicyVerdict Admit(string key, Timestamp recordedAt) =>
        new(VoicePresenceEventPolicyDecision.Admit, key, recordedAt);

    public static VoicePresenceEventPolicyVerdict Drop(VoicePresenceEventPolicyDecision decision) =>
        new(decision, string.Empty, null);
}

public enum VoicePresenceEventPolicyDecision
{
    Admit,
    DropStale,
    DropDuplicate,
}
