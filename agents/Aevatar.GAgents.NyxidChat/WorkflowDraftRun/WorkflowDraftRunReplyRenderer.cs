using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

public sealed record WorkflowDraftRunRenderedFrame(
    string Text,
    bool IsTerminal,
    bool IsFailure,
    string ErrorCode = "");

public sealed class WorkflowDraftRunReplyRenderer
{
    public WorkflowDraftRunRenderedFrame? Render(WorkflowRunEventEnvelope frame, string accumulatedText)
    {
        ArgumentNullException.ThrowIfNull(frame);

        switch (frame.EventCase)
        {
            case WorkflowRunEventEnvelope.EventOneofCase.TextMessageContent:
                var delta = frame.TextMessageContent?.Delta ?? string.Empty;
                if (string.IsNullOrEmpty(delta))
                    return null;
                return new WorkflowDraftRunRenderedFrame(accumulatedText + delta, false, false);

            case WorkflowRunEventEnvelope.EventOneofCase.RunFinished:
                var finalText = accumulatedText;
                if (string.IsNullOrWhiteSpace(finalText) &&
                    frame.RunFinished?.Result?.Is(WorkflowRunResultPayload.Descriptor) == true)
                {
                    finalText = frame.RunFinished.Result.Unpack<WorkflowRunResultPayload>().Output;
                }
                return new WorkflowDraftRunRenderedFrame(
                    string.IsNullOrWhiteSpace(finalText) ? "Workflow 已完成。" : finalText,
                    true,
                    false);

            case WorkflowRunEventEnvelope.EventOneofCase.RunError:
                var message = frame.RunError?.Message;
                return new WorkflowDraftRunRenderedFrame(
                    string.IsNullOrWhiteSpace(message) ? "Workflow 运行失败。" : $"Workflow 运行失败: {message}",
                    true,
                    true,
                    frame.RunError?.Code ?? "workflow_run_error");

            case WorkflowRunEventEnvelope.EventOneofCase.RunStopped:
                var reason = frame.RunStopped?.Reason;
                return new WorkflowDraftRunRenderedFrame(
                    string.IsNullOrWhiteSpace(reason) ? "Workflow 已停止。" : $"Workflow 已停止: {reason}",
                    true,
                    true,
                    "workflow_run_stopped");

            default:
                return null;
        }
    }
}
