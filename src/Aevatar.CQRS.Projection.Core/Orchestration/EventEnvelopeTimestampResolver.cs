namespace Aevatar.CQRS.Projection.Core.Orchestration;

public static class EventEnvelopeTimestampResolver
{
    public static DateTimeOffset Resolve(EventEnvelope envelope, DateTimeOffset fallbackUtcNow)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var ts = envelope.Timestamp;
        if (ts == null)
            return fallbackUtcNow;

        var dt = ts.ToDateTime();
        if (dt.Kind != DateTimeKind.Utc)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        return new DateTimeOffset(dt);
    }
}
