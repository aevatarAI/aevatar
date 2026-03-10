namespace Aevatar.Foundation.Abstractions.Streaming;

/// <summary>
/// Loop-prevention helper based on runtime forwarding visited stream ids.
/// </summary>
public static class ForwardingVisitChain
{
    public static bool Contains(EventEnvelope envelope, string actorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(actorId))
            return false;

        return envelope.Runtime?.Forwarding?.VisitedStreamIds.Contains(actorId) == true;
    }

    public static void AppendIfMissing(EventEnvelope envelope, string actorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var visited = envelope.EnsureRuntime().EnsureForwarding().VisitedStreamIds;
        if (visited.Contains(actorId))
            return;

        visited.Add(actorId);
    }
}
