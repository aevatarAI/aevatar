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
                TopologyAudience.Children,
                TopologyAudience.ParentAndChildren,
            ],
        };
    }

    public static bool Matches(StreamForwardingBinding binding, EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(envelope);

        var direction = envelope.Route.GetTopologyAudience();
        if (binding.DirectionFilter.Count > 0 && !binding.DirectionFilter.Contains(direction))
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

        return !ForwardingVisitChain.Contains(envelope, targetStreamId);
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
        ForwardingVisitChain.AppendIfMissing(forwarded, sourceStreamId);
        var forwarding = forwarded.EnsureRuntime().EnsureForwarding();
        forwarding.SourceStreamId = sourceStreamId;
        forwarding.TargetStreamId = targetStreamId;
        forwarding.Mode = forwardingMode switch
        {
            StreamForwardingMode.TransitOnly => StreamForwardingHandleMode.TransitOnly,
            _ => StreamForwardingHandleMode.HandleThenForward,
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

        return StreamForwardingEnvelopeState.IsForwarded(envelope) &&
               string.Equals(StreamForwardingEnvelopeState.GetTargetStreamId(envelope), targetStreamId, StringComparison.Ordinal);
    }

    public static bool IsTransitOnlyForwarding(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return StreamForwardingEnvelopeState.GetMode(envelope) == StreamForwardingHandleMode.TransitOnly;
    }

    public static bool ShouldSkipTransitOnlyHandling(string selfActorId, EventEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selfActorId);
        ArgumentNullException.ThrowIfNull(envelope);

        var direction = envelope.Route.GetTopologyAudience();
        if (direction is not TopologyAudience.Children and not TopologyAudience.ParentAndChildren)
            return false;

        return IsForwardedEnvelopeForTarget(envelope, selfActorId) &&
               IsTransitOnlyForwarding(envelope);
    }
}
