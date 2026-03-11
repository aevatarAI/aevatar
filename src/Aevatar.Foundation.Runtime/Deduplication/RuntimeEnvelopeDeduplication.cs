namespace Aevatar.Foundation.Runtime.Deduplication;

/// <summary>
/// Builds stable runtime deduplication keys for inbound envelopes.
/// </summary>
public static class RuntimeEnvelopeDeduplication
{
    public static bool TryBuildDedupKey(
        string actorId,
        EventEnvelope envelope,
        out string dedupKey)
    {
        dedupKey = string.Empty;
        if (string.IsNullOrWhiteSpace(actorId))
            return false;

        var originId = ResolveOriginId(envelope);
        if (string.IsNullOrWhiteSpace(originId))
            return false;

        dedupKey = $"{actorId}:{originId}:{GetAttempt(envelope)}";
        return true;
    }

    public static string? ResolveOriginId(EventEnvelope envelope)
    {
        var dedupOriginId = envelope.Runtime?.Deduplication?.OperationId;
        if (!string.IsNullOrWhiteSpace(dedupOriginId))
        {
            return dedupOriginId;
        }

        var retryOriginId = envelope.Runtime?.Retry?.OriginEventId;
        if (!string.IsNullOrWhiteSpace(retryOriginId))
        {
            return retryOriginId;
        }

        return string.IsNullOrWhiteSpace(envelope.Id) ? null : envelope.Id;
    }

    public static int GetAttempt(EventEnvelope envelope)
    {
        var attempt = envelope.Runtime?.Retry?.Attempt ?? 0;
        return attempt > 0 ? attempt : 0;
    }
}
