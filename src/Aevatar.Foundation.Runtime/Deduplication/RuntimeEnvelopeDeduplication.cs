using Aevatar.Foundation.Abstractions.Propagation;

namespace Aevatar.Foundation.Runtime.Deduplication;

/// <summary>
/// Builds stable runtime deduplication keys for inbound envelopes.
/// </summary>
public static class RuntimeEnvelopeDeduplication
{
    public const string RetryAttemptMetadataKey = "aevatar.retry.attempt";
    public const string RetryOriginEventIdMetadataKey = "aevatar.retry.origin_event_id";

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
        if (envelope.Metadata.TryGetValue(EnvelopeMetadataKeys.DedupOriginId, out var dedupOriginId) &&
            !string.IsNullOrWhiteSpace(dedupOriginId))
        {
            return dedupOriginId;
        }

        if (envelope.Metadata.TryGetValue(RetryOriginEventIdMetadataKey, out var retryOriginId) &&
            !string.IsNullOrWhiteSpace(retryOriginId))
        {
            return retryOriginId;
        }

        return string.IsNullOrWhiteSpace(envelope.Id) ? null : envelope.Id;
    }

    public static int GetAttempt(EventEnvelope envelope)
    {
        if (!envelope.Metadata.TryGetValue(RetryAttemptMetadataKey, out var value))
            return 0;

        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;
    }
}
