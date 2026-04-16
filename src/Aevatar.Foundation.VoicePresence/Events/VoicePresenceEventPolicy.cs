using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Events;

/// <summary>
/// Admission policy for buffered voice event injection.
/// </summary>
public sealed class VoicePresenceEventPolicy
{
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan DedupeWindow { get; init; } = TimeSpan.FromSeconds(2);

    private readonly LinkedList<RecentEventEntry> _recent = [];
    private readonly HashSet<string> _recentKeys = [];

    public VoicePresenceEventPolicyDecision Evaluate(EventEnvelope envelope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        Prune(now);

        var observedAt = envelope.Timestamp?.ToDateTimeOffset() ?? now;
        if (now - observedAt > StaleAfter)
            return VoicePresenceEventPolicyDecision.DropStale;

        var key = BuildKey(envelope);
        if (!_recentKeys.Add(key))
            return VoicePresenceEventPolicyDecision.DropDuplicate;

        _recent.AddLast(new RecentEventEntry(key, now));
        return VoicePresenceEventPolicyDecision.Admit;
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - DedupeWindow;
        while (_recent.First is { } first && first.Value.RecordedAt < cutoff)
        {
            _recentKeys.Remove(first.Value.Key);
            _recent.RemoveFirst();
        }
    }

    private static string BuildKey(EventEnvelope envelope)
    {
        if (envelope.Payload == null)
            return "payload:null";

        var payloadBytes = envelope.Payload.Value.IsEmpty
            ? string.Empty
            : Convert.ToBase64String(envelope.Payload.Value.ToByteArray());

        return $"{envelope.Payload.TypeUrl}|{payloadBytes}";
    }

    private readonly record struct RecentEventEntry(string Key, DateTimeOffset RecordedAt);
}

public enum VoicePresenceEventPolicyDecision
{
    Admit,
    DropStale,
    DropDuplicate,
}
