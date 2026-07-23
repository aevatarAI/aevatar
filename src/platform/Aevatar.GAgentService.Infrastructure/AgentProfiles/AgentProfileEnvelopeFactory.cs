using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.AgentProfiles;

internal static class AgentProfileEnvelopeFactory
{
    private const string PublisherId = "gagent-service.agent-profiles";

    public static EventEnvelope Create(
        string targetActorId,
        AgentProfileOperationFact operation,
        IMessage command) =>
        new()
        {
            Id = operation.CommandId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(command),
            Route = EnvelopeRouteSemantics.CreateDirect(PublisherId, targetActorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = operation.CorrelationId,
            },
        };
}
