using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowChatRequestEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
{
    public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
    {
        if (!context.Metadata.TryGetValue(WorkflowRunCommandMetadataKeys.CommandId, out var commandId) ||
            string.IsNullOrWhiteSpace(commandId))
            throw new InvalidOperationException($"Missing metadata '{WorkflowRunCommandMetadataKeys.CommandId}'.");

        var sessionId = context.Metadata.TryGetValue(WorkflowRunCommandMetadataKeys.SessionId, out var metadataSessionId) &&
                        !string.IsNullOrWhiteSpace(metadataSessionId)
            ? metadataSessionId
            : context.CommandId;

        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = sessionId,
        };
        chatRequest.Metadata[ChatRequestMetadataKeys.CommandId] = commandId;
        foreach (var item in context.Metadata)
            chatRequest.Metadata[item.Key] = item.Value;

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            PublisherId = "api",
            Direction = EventDirection.Self,
            CorrelationId = context.CommandId,
            TargetActorId = context.TargetId,
        };
        foreach (var item in context.Metadata)
            envelope.Metadata[item.Key] = item.Value;
        return envelope;
    }
}
