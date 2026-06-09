namespace Aevatar.GAgents.NyxidChat.WorkflowDraftRun;

public sealed record WorkflowDraftRunRenderedFrame(
    string Text,
    bool IsTerminal,
    bool IsFailure,
    string ErrorCode = "");

public sealed class WorkflowDraftRunReplyRenderer
{
    public WorkflowDraftRunRenderedFrame? Render(ChannelWorkflowDraftRunFrame frame, string accumulatedText)
    {
        ArgumentNullException.ThrowIfNull(frame);

        switch (frame.FrameCase)
        {
            case ChannelWorkflowDraftRunFrame.FrameOneofCase.TextMessageContent:
                var delta = frame.TextMessageContent?.Delta ?? string.Empty;
                if (string.IsNullOrEmpty(delta))
                    return null;
                return new WorkflowDraftRunRenderedFrame(accumulatedText + delta, false, false);

            case ChannelWorkflowDraftRunFrame.FrameOneofCase.RunFinished:
                var finalText = accumulatedText;
                if (string.IsNullOrWhiteSpace(finalText))
                    finalText = frame.RunFinished?.ResultOutput ?? string.Empty;
                return new WorkflowDraftRunRenderedFrame(
                    string.IsNullOrWhiteSpace(finalText) ? "Workflow 已完成。" : finalText,
                    true,
                    false);

            case ChannelWorkflowDraftRunFrame.FrameOneofCase.RunError:
                var message = frame.RunError?.Message;
                return new WorkflowDraftRunRenderedFrame(
                    string.IsNullOrWhiteSpace(message) ? "Workflow 运行失败。" : $"Workflow 运行失败: {message}",
                    true,
                    true,
                    string.IsNullOrWhiteSpace(frame.RunError?.Code) ? "workflow_run_error" : frame.RunError.Code);

            case ChannelWorkflowDraftRunFrame.FrameOneofCase.RunStopped:
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
