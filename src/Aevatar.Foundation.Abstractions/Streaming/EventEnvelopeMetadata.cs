namespace Aevatar.Foundation.Abstractions.Streaming;

/// <summary>
/// Metadata key definitions for stream forwarding envelopes.
/// </summary>
public static class StreamForwardingEnvelopeMetadata
{
    public const string ForwardedValue = "1";

    public const string ForwardedKey = "__stream_forwarded";
    public const string ForwardSourceKey = "__stream_forward_source";
    public const string ForwardTargetKey = "__stream_forward_target";
    public const string ForwardModeKey = "__stream_forward_mode";

    public const string ForwardModeTransit = "transit";
    public const string ForwardModeHandle = "handle";
}

/// <summary>
/// Publisher-chain metadata helper used for loop prevention.
/// </summary>
public static class PublisherChainMetadata
{
    public const string PublishersMetadataKey = "__publishers";

    public static bool Contains(EventEnvelope envelope, string actorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Contains(
            envelope.Metadata.GetValueOrDefault(PublishersMetadataKey),
            actorId);
    }

    public static bool Contains(string? chain, string actorId)
    {
        if (string.IsNullOrWhiteSpace(chain) || string.IsNullOrWhiteSpace(actorId))
            return false;

        var span = chain.AsSpan();
        while (!span.IsEmpty)
        {
            var comma = span.IndexOf(',');
            ReadOnlySpan<char> token;
            if (comma < 0)
            {
                token = span;
                span = [];
            }
            else
            {
                token = span[..comma];
                span = span[(comma + 1)..];
            }

            token = token.Trim();
            if (token.Equals(actorId.AsSpan(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static void AppendIfMissing(EventEnvelope envelope, string actorId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        if (!envelope.Metadata.TryGetValue(PublishersMetadataKey, out var chain) ||
            string.IsNullOrWhiteSpace(chain))
        {
            envelope.Metadata[PublishersMetadataKey] = actorId;
            return;
        }

        if (Contains(chain, actorId))
            return;

        envelope.Metadata[PublishersMetadataKey] = $"{chain},{actorId}";
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
