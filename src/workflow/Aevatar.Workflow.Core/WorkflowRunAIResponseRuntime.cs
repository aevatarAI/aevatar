namespace Aevatar.Workflow.Core;

internal sealed class WorkflowRunAIResponseRuntime
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
