namespace Aevatar.Foundation.Abstractions.Streaming;

/// <summary>
/// Shared stream-forwarding rules used by local runtime and Orleans runtime.
/// </summary>
public static class StreamForwardingRules
{
    public static StreamForwardingBinding CreateHierarchyBinding(
        string sourceStreamId,
        string targetStreamId,
        StreamForwardingMode forwardingMode = StreamForwardingMode.HandleThenForward)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);

        return new StreamForwardingBinding
        {
            SourceStreamId = sourceStreamId,
            TargetStreamId = targetStreamId,
            ForwardingMode = forwardingMode,
            DirectionFilter =
            [
                EventDirection.Down,
                EventDirection.Both,
            ],
        };
    }

    public static bool Matches(StreamForwardingBinding binding, EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(envelope);

        if (binding.DirectionFilter.Count > 0 && !binding.DirectionFilter.Contains(envelope.Direction))
            return false;

        if (binding.EventTypeFilter.Count == 0)
            return true;

        var typeUrl = envelope.Payload?.TypeUrl;
        return !string.IsNullOrWhiteSpace(typeUrl) && binding.EventTypeFilter.Contains(typeUrl);
    }

    public static bool IsTargetDispatchAllowed(string sourceStreamId, string targetStreamId, EventEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);
        ArgumentNullException.ThrowIfNull(envelope);

        if (string.Equals(sourceStreamId, targetStreamId, StringComparison.Ordinal))
            return false;

        return !PublisherChainMetadata.Contains(envelope, targetStreamId);
    }

    public static EventEnvelope BuildForwardedEnvelope(
        EventEnvelope envelope,
        string sourceStreamId,
        string targetStreamId,
        StreamForwardingMode forwardingMode)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);

        var forwarded = envelope.Clone();
        PublisherChainMetadata.AppendIfMissing(forwarded, sourceStreamId);
        forwarded.Metadata[StreamForwardingEnvelopeMetadata.ForwardedKey] =
            StreamForwardingEnvelopeMetadata.ForwardedValue;
        forwarded.Metadata[StreamForwardingEnvelopeMetadata.ForwardSourceKey] = sourceStreamId;
        forwarded.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey] = targetStreamId;
        forwarded.Metadata[StreamForwardingEnvelopeMetadata.ForwardModeKey] = forwardingMode switch
        {
            StreamForwardingMode.TransitOnly => StreamForwardingEnvelopeMetadata.ForwardModeTransit,
            _ => StreamForwardingEnvelopeMetadata.ForwardModeHandle,
        };

        return forwarded;
    }

    public static bool TryBuildForwardedEnvelope(
        string sourceStreamId,
        StreamForwardingBinding binding,
        EventEnvelope envelope,
        out EventEnvelope? forwardedEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStreamId);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!Matches(binding, envelope) ||
            !IsTargetDispatchAllowed(sourceStreamId, binding.TargetStreamId, envelope))
        {
            forwardedEnvelope = null;
            return false;
        }

        forwardedEnvelope = BuildForwardedEnvelope(
            envelope,
            sourceStreamId,
            binding.TargetStreamId,
            binding.ForwardingMode);
        return true;
    }

    public static bool IsForwardedEnvelopeForTarget(EventEnvelope envelope, string targetStreamId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetStreamId);

        if (!envelope.Metadata.TryGetValue(StreamForwardingEnvelopeMetadata.ForwardedKey, out var forwarded) ||
            !string.Equals(forwarded, StreamForwardingEnvelopeMetadata.ForwardedValue, StringComparison.Ordinal))
        {
            return false;
        }

        return envelope.Metadata.TryGetValue(StreamForwardingEnvelopeMetadata.ForwardTargetKey, out var target) &&
               string.Equals(target, targetStreamId, StringComparison.Ordinal);
    }

    public static bool IsTransitOnlyForwarding(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return envelope.Metadata.TryGetValue(StreamForwardingEnvelopeMetadata.ForwardModeKey, out var mode) &&
               string.Equals(mode, StreamForwardingEnvelopeMetadata.ForwardModeTransit, StringComparison.Ordinal);
    }

    public static bool ShouldSkipTransitOnlyHandling(string selfActorId, EventEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfActorId);
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Direction is not EventDirection.Down and not EventDirection.Both)
            return false;

        return IsForwardedEnvelopeForTarget(envelope, selfActorId) &&
               IsTransitOnlyForwarding(envelope);
    }
}
