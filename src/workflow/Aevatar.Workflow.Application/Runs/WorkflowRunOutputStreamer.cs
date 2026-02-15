using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunOutputStreamer : IWorkflowRunOutputStreamer
{
    public async Task StreamAsync(
        IWorkflowRunEventSink sink,
        string runId,
        Func<WorkflowOutputFrame, CancellationToken, ValueTask> emitAsync,
        CancellationToken ct = default)
    {
        await foreach (var evt in sink.ReadAllAsync(ct))
        {
            var frame = WorkflowOutputFrameMapper.Map(evt);
            await emitAsync(frame, ct);
            if (IsTerminalEventForRun(evt, runId))
                break;
        }
    }

    private static bool IsTerminalEventForRun(WorkflowRunEvent evt, string runId)
    {
        return evt switch
        {
            WorkflowRunFinishedEvent finished => string.Equals(finished.RunId, runId, StringComparison.Ordinal),
            WorkflowRunErrorEvent error => string.Equals(error.RunId, runId, StringComparison.Ordinal),
            _ => false,
        };
    }
}
