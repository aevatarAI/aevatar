using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Maintenance;

// Refactor (issue1056/r3-consensus): Old pattern: hosted cleanup encoded lease
// progress as marker StateEvents and replayed them directly from IEventStore.
// New principle: cleanup coordination uses typed protobuf commands addressed to
// the approved coordinator actor; no generic lease service or marker stream.
public static class RetiredActorCleanupCoordinatorEnvelopeFactory
{
    public const string CoordinatorActorId = "maintenance.retired-actor-cleanup-coordinator";
    private const string PublisherActorId = "retired-actor-cleanup.hosted-service";

    public static EventEnvelope Create(IMessage command, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherActorId, CoordinatorActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = string.IsNullOrWhiteSpace(correlationId)
                    ? Guid.NewGuid().ToString("N")
                    : correlationId.Trim(),
            },
        };
    }
}
