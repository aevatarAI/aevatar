namespace Aevatar.Foundation.Abstractions.Streaming;

/// <summary>
/// Runtime forwarding state readers for forwarded envelopes.
/// </summary>
public static class StreamForwardingEnvelopeState
{
    public static bool IsForwarded(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var forwarding = envelope.Runtime?.Forwarding;
        return forwarding != null && !string.IsNullOrWhiteSpace(forwarding.TargetStreamId);
    }

    public static string? GetSourceStreamId(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Runtime?.Forwarding?.SourceStreamId;
    }

    public static string? GetTargetStreamId(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Runtime?.Forwarding?.TargetStreamId;
    }

    public static StreamForwardingHandleMode GetMode(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Runtime?.Forwarding?.Mode ?? StreamForwardingHandleMode.Unspecified;
    }
}
