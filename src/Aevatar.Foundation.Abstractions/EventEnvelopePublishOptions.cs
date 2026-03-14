namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// Explicit outbound envelope options with narrow, non-runtime-owned overrides.
/// </summary>
public sealed class EventEnvelopePublishOptions
{
    public EventEnvelopePropagationOverrides? Propagation { get; init; }

    public EventEnvelopeDeliveryOptions? Delivery { get; init; }

    public EventEnvelopePublishOptions DeepClone() =>
        new()
        {
            Propagation = Propagation?.DeepClone(),
            Delivery = Delivery?.DeepClone(),
        };
}

/// <summary>
/// Explicit propagation overrides available to application code.
/// </summary>
public sealed class EventEnvelopePropagationOverrides
{
    public string? CorrelationId { get; init; }

    public string? CausationEventId { get; init; }

    public TraceContext? Trace { get; init; }

    public IDictionary<string, string> Baggage { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public EventEnvelopePropagationOverrides DeepClone()
    {
        var clone = new EventEnvelopePropagationOverrides
        {
            CorrelationId = CorrelationId,
            CausationEventId = CausationEventId,
            Trace = Trace?.Clone(),
        };

        foreach (var pair in Baggage)
            clone.Baggage[pair.Key] = pair.Value;

        return clone;
    }
}

/// <summary>
/// Explicit delivery controls available to application code.
/// </summary>
public sealed class EventEnvelopeDeliveryOptions
{
    public string? DeduplicationOperationId { get; init; }

    public EventEnvelopeDeliveryOptions DeepClone() =>
        new()
        {
            DeduplicationOperationId = DeduplicationOperationId,
        };
}
