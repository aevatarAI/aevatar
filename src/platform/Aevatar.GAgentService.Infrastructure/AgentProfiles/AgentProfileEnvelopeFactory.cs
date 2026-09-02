using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Infrastructure.AgentProfiles;

internal static class AgentProfileEnvelopeFactory
{
    private const string PublisherId = "gagent-service.agent-profile.actor-port";

    internal static EventEnvelope Create(
        string targetActorId,
        AgentProfileOperationFact operation,
        IMessage command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(command);
        ValidateOperation(operation);

        return new EventEnvelope
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

    internal static void ValidateOperation(AgentProfileOperationFact? operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateIdentifier(operation.OperationId, "operation_id");
        ValidateIdentifier(operation.CommandId, "command_id");
        ValidateIdentifier(operation.CorrelationId, "correlation_id");
    }

    private static void ValidateIdentifier(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"A stable {fieldName} is required.", fieldName);
    }
}
