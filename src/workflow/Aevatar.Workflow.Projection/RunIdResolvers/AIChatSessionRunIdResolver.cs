using Aevatar.AI.Abstractions;
using AIEvents = Aevatar.AI.Abstractions;

namespace Aevatar.Workflow.Projection.RunIdResolvers;

public sealed class AIChatMessageRunIdResolver : IWorkflowExecutionRunIdResolver
{
    public int Order => 100;

    public bool TryResolve(EventEnvelope envelope, out string? runId)
    {
        runId = null;
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        if (payload.Is(AIEvents.TextMessageStartEvent.Descriptor))
            return ChatMessageKeys.TryParseWorkflowRunId(payload.Unpack<AIEvents.TextMessageStartEvent>().MessageId, out runId);

        if (payload.Is(AIEvents.TextMessageContentEvent.Descriptor))
            return ChatMessageKeys.TryParseWorkflowRunId(payload.Unpack<AIEvents.TextMessageContentEvent>().MessageId, out runId);

        if (payload.Is(AIEvents.TextMessageEndEvent.Descriptor))
            return ChatMessageKeys.TryParseWorkflowRunId(payload.Unpack<AIEvents.TextMessageEndEvent>().MessageId, out runId);

        if (payload.Is(AIEvents.ChatResponseEvent.Descriptor))
            return ChatMessageKeys.TryParseWorkflowRunId(payload.Unpack<AIEvents.ChatResponseEvent>().MessageId, out runId);

        return false;
    }
}
