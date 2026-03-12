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
        var sessionId = context.Metadata.TryGetValue(WorkflowRunCommandMetadataKeys.SessionId, out var metadataSessionId) &&
                        !string.IsNullOrWhiteSpace(metadataSessionId)
            ? metadataSessionId
            : context.CorrelationId;

        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = sessionId,
        };
        foreach (var (key, value) in context.Metadata)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;
            chatRequest.Metadata[normalizedKey] = normalizedValue;
        }
        chatRequest.Metadata[WorkflowRunCommandMetadataKeys.SessionId] = sessionId;

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            PublisherId = "api",
            Direction = EventDirection.Self,
            CorrelationId = context.CorrelationId,
            TargetActorId = context.TargetId,
        };
        return envelope;
    }
}
