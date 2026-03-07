using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;

namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunAIResponseRuntime : IWorkflowResponseHandler
{
    private readonly WorkflowRunLlmRuntime _llmRuntime;
    private readonly WorkflowRunEvaluationRuntime _evaluationRuntime;
    private readonly WorkflowRunReflectRuntime _reflectRuntime;

    public WorkflowRunAIResponseRuntime(
        WorkflowRunLlmRuntime llmRuntime,
        WorkflowRunEvaluationRuntime evaluationRuntime,
        WorkflowRunReflectRuntime reflectRuntime)
    {
        _llmRuntime = llmRuntime ?? throw new ArgumentNullException(nameof(llmRuntime));
        _evaluationRuntime = evaluationRuntime ?? throw new ArgumentNullException(nameof(evaluationRuntime));
        _reflectRuntime = reflectRuntime ?? throw new ArgumentNullException(nameof(reflectRuntime));
    }

    public async Task<bool> TryHandleAsync(EventEnvelope envelope, string defaultPublisherId, CancellationToken ct)
    {
        var payload = envelope.Payload;
        if (payload == null)
            return false;

        if (payload.Is(TextMessageEndEvent.Descriptor))
        {
            var evt = payload.Unpack<TextMessageEndEvent>();
            await HandleLlmLikeResponseAsync(
                evt.SessionId,
                evt.Content ?? string.Empty,
                envelope.PublisherId,
                ct);
            return true;
        }

        if (payload.Is(ChatResponseEvent.Descriptor))
        {
            var evt = payload.Unpack<ChatResponseEvent>();
            await HandleLlmLikeResponseAsync(
                evt.SessionId,
                evt.Content ?? string.Empty,
                string.IsNullOrWhiteSpace(envelope.PublisherId) ? defaultPublisherId : envelope.PublisherId,
                ct);
            return true;
        }

        return false;
    }

    public async Task HandleLlmLikeResponseAsync(
        string? sessionId,
        string content,
        string publisherId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        if (await _llmRuntime.TryHandleLlmLikeResponseAsync(sessionId, content, publisherId, ct))
            return;

        if (await _evaluationRuntime.TryHandleLlmLikeResponseAsync(sessionId, content, ct))
            return;

        await _reflectRuntime.TryHandleLlmLikeResponseAsync(sessionId, content, ct);
    }
}
