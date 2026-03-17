using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeCommandEnvelopeFactory
{
    private const string PublisherActorId = "projection.scope.port";

    public static EventEnvelope Create(IMessage payload, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, string.Empty),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = correlationId ?? string.Empty,
            },
        };
    }
}
