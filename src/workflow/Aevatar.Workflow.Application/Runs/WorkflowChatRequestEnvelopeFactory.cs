using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowChatRequestEnvelopeFactory : IWorkflowChatRequestEnvelopeFactory
{
    public EventEnvelope Create(string prompt, string runId)
    {
        var chatRequest = new ChatRequestEvent
        {
            Prompt = prompt,
            MessageId = CreateInternalChatMessageId(),
        };
        chatRequest.Metadata[ChatRequestMetadataKeys.RunId] = runId;

        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            PublisherId = "api",
            Direction = EventDirection.Self,
        };
    }

    private static string CreateInternalChatMessageId() => $"chat-{Guid.NewGuid():N}";
}
