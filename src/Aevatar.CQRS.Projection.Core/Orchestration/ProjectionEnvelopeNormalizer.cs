using Aevatar.Foundation.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

public static class ProjectionEnvelopeNormalizer
{
    public static EventEnvelope? Normalize(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Route == null || envelope.Route.RouteCase == EnvelopeRoute.RouteOneofCase.None)
            return envelope;

        if (envelope.Route.IsDirect())
            return null;

        if (envelope.Route.IsTopologyPublication())
            return envelope;

        if (!envelope.Route.IsObserverPublication())
            return null;

        if (envelope.Payload?.Is(CommittedStateEventPublished.Descriptor) != true)
            return envelope;

        var published = envelope.Payload.Unpack<CommittedStateEventPublished>();
        if (published.StateEvent?.EventData == null)
            return null;

        var normalized = envelope.Clone();
        normalized.Payload = published.StateEvent.EventData.Clone();
        if (published.StateEvent.Timestamp != null)
            normalized.Timestamp = published.StateEvent.Timestamp.Clone();
        return normalized;
    }
}
