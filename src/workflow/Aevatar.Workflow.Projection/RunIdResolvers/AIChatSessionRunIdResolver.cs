using Aevatar.AI.Abstractions;
using AIEvents = Aevatar.AI.Abstractions;

namespace Aevatar.Workflow.Projection.RunIdResolvers;

public sealed class AIChatSessionRunIdResolver : IWorkflowExecutionRunIdResolver
{
    public int Order => 100;

    public bool TryResolve(EventEnvelope envelope, out string? runId)
    {
        runId = null;
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        if (payload.Is(AIEvents.TextMessageStartEvent.Descriptor))
            return ChatSessionKeys.TryParseWorkflowRunId(payload.Unpack<AIEvents.TextMessageStartEvent>().SessionId, out runId);

        if (payload.Is(AIEvents.TextMessageContentEvent.Descriptor))
            return ChatSessionKeys.TryParseWorkflowRunId(payload.Unpack<AIEvents.TextMessageContentEvent>().SessionId, out runId);

        if (payload.Is(AIEvents.TextMessageEndEvent.Descriptor))
            return ChatSessionKeys.TryParseWorkflowRunId(payload.Unpack<AIEvents.TextMessageEndEvent>().SessionId, out runId);

        if (payload.Is(AIEvents.ChatResponseEvent.Descriptor))
            return ChatSessionKeys.TryParseWorkflowRunId(payload.Unpack<AIEvents.ChatResponseEvent>().SessionId, out runId);

        return false;
    }
}
