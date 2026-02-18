using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowChatRequestEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
{
    public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandCorrelation correlation)
    {
        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = correlation.SessionId,
        };
        chatRequest.Metadata[ChatRequestMetadataKeys.RunId] = correlation.ExecutionId;
        chatRequest.Metadata[CommandCorrelationMetadataKeys.SessionId] = correlation.SessionId;
        chatRequest.Metadata[CommandCorrelationMetadataKeys.ActorId] = correlation.ActorId;
        chatRequest.Metadata[CommandCorrelationMetadataKeys.CorrelationId] = correlation.CorrelationId;

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            PublisherId = "api",
            Direction = EventDirection.Self,
            CorrelationId = correlation.CorrelationId,
            TargetActorId = correlation.ActorId,
        };
        envelope.Metadata[CommandCorrelationMetadataKeys.ExecutionId] = correlation.ExecutionId;
        envelope.Metadata[CommandCorrelationMetadataKeys.SessionId] = correlation.SessionId;
        envelope.Metadata[CommandCorrelationMetadataKeys.ActorId] = correlation.ActorId;
        envelope.Metadata[CommandCorrelationMetadataKeys.CorrelationId] = correlation.CorrelationId;
        return envelope;
    }
}
