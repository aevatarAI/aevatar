namespace Aevatar.Foundation.Runtime.Delivery;

/// <summary>
/// Resolves stable delivery lineage and retry-attempt identity from an envelope.
/// These values identify a delivery; they do not record completion or suppress redelivery.
/// </summary>
public static class RuntimeEnvelopeDeliveryIdentity
{
    public static string? ResolveDeliveryLineageId(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var operationId = envelope.Runtime?.DeliveryIdentity?.OperationId;
        if (!string.IsNullOrWhiteSpace(operationId))
            return operationId;

        var retryOriginId = envelope.Runtime?.Retry?.OriginEventId;
        if (!string.IsNullOrWhiteSpace(retryOriginId))
            return retryOriginId;

        return string.IsNullOrWhiteSpace(envelope.Id) ? null : envelope.Id;
    }

    public static int GetAttempt(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var attempt = envelope.Runtime?.Retry?.Attempt ?? 0;
        return attempt > 0 ? attempt : 0;
    }
}
