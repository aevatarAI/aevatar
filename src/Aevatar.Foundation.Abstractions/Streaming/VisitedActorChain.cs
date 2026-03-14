namespace Aevatar.Foundation.Abstractions.Streaming;

/// <summary>
/// Runtime visited-actor chain helpers for loop prevention and receiver-side dedup checks.
/// </summary>
public static class VisitedActorChain
{
    public static bool Contains(EventEnvelope envelope, string actorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(actorId))
            return false;

        return envelope.Runtime?.VisitedActorIds.Contains(actorId) == true;
    }

    public static void AppendIfMissing(EventEnvelope envelope, string actorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var visited = envelope.EnsureRuntime().VisitedActorIds;
        if (visited.Contains(actorId))
            return;

        visited.Add(actorId);
    }

    public static void AppendDispatchPublisher(
        EventEnvelope envelope,
        string senderActorId,
        string targetActorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);

        if (string.Equals(senderActorId, targetActorId, StringComparison.Ordinal))
            return;

        AppendIfMissing(envelope, senderActorId);
    }

    public static bool ShouldDropForReceiver(EventEnvelope envelope, string selfActorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(selfActorId);

        return Contains(envelope, selfActorId);
    }
}
